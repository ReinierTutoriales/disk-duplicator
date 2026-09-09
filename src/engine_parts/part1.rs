use crossbeam_channel as mpsc;
use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use walkdir::WalkDir;

const BLOCK: usize = 4 * 1024 * 1024;
const RESERVED_RAM: usize = 512 * 1024 * 1024;
const MIN_QUEUE: usize = 2;
const MAX_QUEUE: usize = 64;
const RETRIES: usize = 2;
const DELIVERY_RETRY_SLEEP: Duration = Duration::from_millis(2);
const STATE_BATCH_FILES: usize = 128;
const STATE_BATCH_INTERVAL: Duration = Duration::from_secs(1);

#[derive(Clone, Copy)]
pub struct CopyOpts {
    pub verify: bool,
    pub skip_same: bool,
    pub keep_going: bool,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum DestPhase {
    Idle,
    Copying,
    Verifying,
    Done,
    Failed,
    Cancelled,
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum CopyMode {
    Fanout,
    PerDestination,
    Fallback,
}

#[derive(Clone)]
pub struct DestProgress {
    pub label: String,
    pub written: u64,
    pub total: u64,
    pub files_done: u64,
    pub files_skip: u64,
    pub files_err: u64,
    pub bps: f64,
    pub phase: DestPhase,
    pub error: Option<String>,
    pub last_file: String,
    pub mode: CopyMode,
    pub queue_depth: usize,
    pub retries: u64,
}

pub struct JobState {
    pub running: AtomicBool,
    pub cancel: AtomicBool,
    pub pause: AtomicBool,
    pub files_total: AtomicU64,
    pub bytes_total: AtomicU64,
    pub buffers_in_flight: Arc<AtomicUsize>,
    pub max_buffers: usize,
    pub fanout: bool,
    pub dests: Mutex<Vec<DestProgress>>,
}

impl JobState {
    pub fn snapshot(&self) -> Vec<DestProgress> {
        self.dests.lock().unwrap().clone()
    }
}

struct BufferBudget {
    in_flight: Mutex<usize>,
    cv: Condvar,
    max: usize,
    gauge: Arc<AtomicUsize>,
}

impl BufferBudget {
    fn new(max: usize, gauge: Arc<AtomicUsize>) -> Arc<Self> {
        Arc::new(Self { in_flight: Mutex::new(0), cv: Condvar::new(), max, gauge })
    }

    fn acquire(&self, state: &JobState) -> bool {
        let mut n = self.in_flight.lock().unwrap();
        while *n >= self.max {
            if state.cancel.load(Ordering::Relaxed) { return false; }
            let (next, _) = self.cv.wait_timeout(n, Duration::from_millis(80)).unwrap();
            n = next;
        }
        if state.cancel.load(Ordering::Relaxed) { return false; }
        *n += 1;
        self.gauge.store(*n, Ordering::Relaxed);
        true
    }

    fn release(&self) {
        let mut n = self.in_flight.lock().unwrap();
        *n = n.saturating_sub(1);
        self.gauge.store(*n, Ordering::Relaxed);
        self.cv.notify_one();
    }
}

struct Buffer { data: Box<[u8]>, budget: Arc<BufferBudget> }
type BufferRef = Arc<Buffer>;
impl Drop for Buffer { fn drop(&mut self) { self.budget.release(); } }

#[derive(Clone, Debug)]
pub(crate) struct FileInfo {
    pub(crate) rel: PathBuf,
    pub(crate) size: u64,
    pub(crate) mtime_ns: u128,
}

enum FanoutItem { Begin(FileInfo), Data(BufferRef), End { hash: [u8; 32] } }
struct DestControl { alive: AtomicBool, queue_depth: AtomicUsize }
impl DestControl {
    fn new() -> Self { Self { alive: AtomicBool::new(true), queue_depth: AtomicUsize::new(0) } }
}

struct CurrentFile {
    info: FileInfo,
    file: Option<File>,
    hasher: blake3::Hasher,
    copied: u64,
    failed: bool,
}

fn queue_depth_for(n_dests: usize) -> usize {
    if n_dests == 0 { return MIN_QUEUE; }
    (RESERVED_RAM / (n_dests * BLOCK)).clamp(MIN_QUEUE, MAX_QUEUE)
}

fn metadata_mtime_ns(meta: &fs::Metadata) -> u128 {
    meta.modified().ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_nanos()).unwrap_or(0)
}

fn expected_mtime(info: &FileInfo) -> Option<SystemTime> {
    if info.mtime_ns == 0 { return None; }
    let secs = info.mtime_ns / 1_000_000_000;
    if secs > u64::MAX as u128 { return None; }
    let nanos = (info.mtime_ns % 1_000_000_000) as u32;
    UNIX_EPOCH.checked_add(Duration::new(secs as u64, nanos))
}

fn validate_source_snapshot(path: &Path, info: &FileInfo) -> Result<(), String> {
    let meta = fs::metadata(path).map_err(|e| format!("origen {}: {e}", path.display()))?;
    if !meta.is_file() || meta.len() != info.size || metadata_mtime_ns(&meta) != info.mtime_ns {
        return Err(format!("origen cambió: {}", path.display()));
    }
    Ok(())
}

fn list_directories(root: &Path) -> Result<Vec<PathBuf>, String> {
    let mut dirs = Vec::new();
    for entry in WalkDir::new(root).follow_links(false) {
        let entry = entry.map_err(|e| format!("origen: {e}"))?;
        if entry.depth() == 0 || !entry.file_type().is_dir() { continue; }
        let rel = entry.path().strip_prefix(root).map_err(|e| e.to_string())?.to_path_buf();
        dirs.push(rel);
    }
    dirs.sort();
    Ok(dirs)
}

fn create_directory_layout(source: &Path, dests: &[PathBuf]) -> Result<(), String> {
    let dirs = list_directories(source)?;
    for dest in dests {
        fs::create_dir_all(dest).map_err(|e| format!("destino {}: {e}", dest.display()))?;
        for rel in &dirs {
            let path = dest.join(rel);
            fs::create_dir_all(&path).map_err(|e| format!("No se pudo crear la carpeta {}: {e}", path.display()))?;
        }
    }
    Ok(())
}

fn dest_inside_source(src: &Path, dst: &Path) -> bool {
    if dst.starts_with(src) { return true; }
    match (src.canonicalize(), dst.canonicalize()) {
        (Ok(s), Ok(d)) => d == s || d.starts_with(&s),
        _ => false,
    }
}

fn wait_pause(state: &JobState) -> bool {
    while state.pause.load(Ordering::Relaxed) && !state.cancel.load(Ordering::Relaxed) {
        thread::sleep(Duration::from_millis(40));
    }
    !state.cancel.load(Ordering::Relaxed)
}

fn set_phase(state: &JobState, slot: usize, phase: DestPhase, err: Option<String>) {
    let mut g = state.dests.lock().unwrap();
    g[slot].phase = phase;
    if err.is_some() { g[slot].error = err; }
}

fn set_error(state: &JobState, slot: usize, msg: String) {
    state.dests.lock().unwrap()[slot].error = Some(msg);
}

fn same_enough(src: &Path, dst: &Path) -> bool {
    let Ok(a) = fs::metadata(src) else { return false; };
    let Ok(b) = fs::metadata(dst) else { return false; };
    if a.len() != b.len() { return false; }
    match (a.modified(), b.modified()) { (Ok(x), Ok(y)) => x == y, _ => false }
}

fn state_dir(dest: &Path) -> PathBuf { dest.join(".disk-duplicator") }
fn state_path(dest: &Path) -> PathBuf { state_dir(dest).join("completed.jsonl") }
fn manifest_path(dest: &Path) -> PathBuf { dest.join("paquetecopies.b3") }

fn state_key(info: &FileInfo) -> String {
    let mut hex = String::new();
    for b in info.rel.to_string_lossy().as_bytes() { hex.push_str(&format!("{b:02x}")); }
    format!("{hex}|{}|{}", info.size, info.mtime_ns)
}

fn load_state(dest: &Path) -> HashSet<String> {
    let Ok(text) = fs::read_to_string(state_path(dest)) else { return HashSet::new(); };
    text.lines().filter_map(|line| {
        let (_, rest) = line.split_once("\"key\":\"")?;
        let (key, _) = rest.split_once('"')?;
        Some(key.to_owned())
    }).collect()
}

struct StateJournal { writer: BufWriter<File>, pending: usize, last_sync: Instant }
impl StateJournal {
    fn open(dest: &Path) -> Result<Self, String> {
        fs::create_dir_all(state_dir(dest)).map_err(|e| format!("state mkdir: {e}"))?;
        let file = OpenOptions::new().create(true).append(true).open(state_path(dest)).map_err(|e| format!("state open: {e}"))?;
        Ok(Self { writer: BufWriter::with_capacity(64 * 1024, file), pending: 0, last_sync: Instant::now() })
    }
    fn append(&mut self, key: &str) -> Result<(), String> {
        writeln!(self.writer, "{{\"key\":\"{key}\"}}").map_err(|e| format!("state write: {e}"))?;
        self.pending += 1;
        if self.pending >= STATE_BATCH_FILES || self.last_sync.elapsed() >= STATE_BATCH_INTERVAL { self.checkpoint()?; }
        Ok(())
    }
    fn checkpoint(&mut self) -> Result<(), String> {
        if self.pending == 0 { return Ok(()); }
        self.writer.flush().map_err(|e| format!("state flush: {e}"))?;
        self.writer.get_ref().sync_all().map_err(|e| format!("state sync: {e}"))?;
        self.pending = 0;
        self.last_sync = Instant::now();
        Ok(())
    }
    fn finish(&mut self) -> Result<(), String> { self.checkpoint() }
}
impl Drop for StateJournal { fn drop(&mut self) { let _ = self.checkpoint(); } }

struct ManifestWriter { writer: BufWriter<File>, dirty: bool }
impl ManifestWriter {
    fn open(dest: &Path) -> Result<Self, String> {
        let file = OpenOptions::new().create(true).append(true).open(manifest_path(dest)).map_err(|e| format!("manifiesto: {e}"))?;
        Ok(Self { writer: BufWriter::with_capacity(64 * 1024, file), dirty: false })
    }
    fn append(&mut self, rel: &Path, hash: &blake3::Hash) -> Result<(), String> {
        let name = rel.to_string_lossy().replace('\\', "/");
        writeln!(self.writer, "{}  {name}", hash.to_hex()).map_err(|e| format!("manifiesto: {e}"))?;
        self.dirty = true;
        Ok(())
    }
    fn finish(&mut self) -> Result<(), String> {
        if !self.dirty { return Ok(()); }
        self.writer.flush().map_err(|e| format!("manifiesto flush: {e}"))?;
        self.writer.get_ref().sync_all().map_err(|e| format!("manifiesto sync: {e}"))?;
        self.dirty = false;
        Ok(())
    }
}
impl Drop for ManifestWriter { fn drop(&mut self) { let _ = self.finish(); } }

fn part_path(dst: &Path) -> PathBuf {
    let mut p = dst.as_os_str().to_os_string();
    p.push(".part");
    PathBuf::from(p)
}
fn cleanup_part(dst: &Path) { let _ = fs::remove_file(part_path(dst)); }

fn commit_part(part: &Path, dst: &Path) -> Result<(), String> {
    if let Some(parent) = dst.parent() { fs::create_dir_all(parent).map_err(|e| format!("mkdir: {e}"))?; }
    fs::rename(part, dst).map_err(|e| format!("rename {}: {e}", dst.display()))
}

fn hash_file(path: &Path, state: Option<&JobState>) -> Result<blake3::Hash, String> {
    let mut f = File::open(path).map_err(|e| format!("verificar: {e}"))?;
    let mut buf = vec![0u8; BLOCK];
    let mut h = blake3::Hasher::new();
    loop {
        if let Some(s) = state { if !wait_pause(s) { return Err("Cancelado".into()); } }
        let n = f.read(&mut buf).map_err(|e| format!("verificar: {e}"))?;
        if n == 0 { break; }
        h.update(&buf[..n]);
    }
    Ok(h.finalize())
}

fn retry_io<T>(state: &JobState, slot: usize, mut op: impl FnMut() -> Result<T, String>) -> Result<T, String> {
    let mut last_err = String::new();
    for attempt in 0..=RETRIES {
        match op() {
            Ok(v) => return Ok(v),
            Err(e) => {
                last_err = e;
                if attempt < RETRIES {
                    state.dests.lock().unwrap()[slot].retries += 1;
                    thread::sleep(Duration::from_millis(75 * (attempt as u64 + 1)));
                    if !wait_pause(state) { return Err("Cancelado".into()); }
                }
            }
        }
    }
    Err(last_err)
}

fn set_mtime(dst: &Path, mtime: SystemTime) -> Result<(), String> {
    let f = OpenOptions::new().write(true).open(dst).map_err(|e| format!("metadata: {e}"))?;
    f.set_modified(mtime).map_err(|e| format!("metadata: {e}"))?;
    f.sync_all().map_err(|e| format!("metadata sync: {e}"))?;
    Ok(())
}

fn record_write_progress(state: &JobState, slot: usize, size: u64, effective_written: &mut u64, start: Instant) {
    *effective_written = effective_written.saturating_add(size);
    let mut g = state.dests.lock().unwrap();
    g[slot].written = g[slot].written.saturating_add(size);
    let secs = start.elapsed().as_secs_f64();
    if secs > 0.0 { g[slot].bps = *effective_written as f64 / secs; }
}

fn rollback_write_progress(state: &JobState, slot: usize, size: u64, effective_written: &mut u64, start: Instant) {
    *effective_written = effective_written.saturating_sub(size);
    let mut g = state.dests.lock().unwrap();
    g[slot].written = g[slot].written.saturating_sub(size);
    let secs = start.elapsed().as_secs_f64();
    g[slot].bps = if secs > 0.0 { *effective_written as f64 / secs } else { 0.0 };
}

fn record_done(state: &JobState, slot: usize) { state.dests.lock().unwrap()[slot].files_done += 1; }
fn record_file_error(state: &JobState, slot: usize, message: String) {
    let mut g = state.dests.lock().unwrap();
    g[slot].files_err = g[slot].files_err.saturating_add(1);
    g[slot].error = Some(message);
}
fn finish_logs(manifest: &mut ManifestWriter, journal: &mut StateJournal) -> Result<(), String> {
    let manifest_result = manifest.finish();
    let journal_result = journal.finish();
    manifest_result.and(journal_result)
}
