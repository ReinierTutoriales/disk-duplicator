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

const BLOCK: usize = 16 * 1024 * 1024;
const RESERVED_RAM: usize = 2048 * 1024 * 1024;
const MIN_QUEUE: usize = 2;
const MAX_QUEUE: usize = 16;
const MAX_FREE_BUFFERS: usize = 16;
const RETRIES: usize = 2;
const STALL_THRESHOLD: Duration = Duration::from_secs(6);
const LONG_OP_THRESHOLD: Duration = Duration::from_secs(60);
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
    pub dests: Mutex<Vec<DestProgress>>,
}

impl JobState {
    pub fn snapshot(&self) -> Vec<DestProgress> {
        self.dests.lock().unwrap().clone()
    }
}

#[derive(Clone, Debug)]
pub(crate) struct FileInfo {
    pub(crate) rel: PathBuf,
    pub(crate) size: u64,
    pub(crate) mtime_ns: u128,
}

struct BufferPool {
    in_flight: Mutex<usize>,
    cv: Condvar,
    max: usize,
    gauge: Arc<AtomicUsize>,
    free: Mutex<Vec<Vec<u8>>>,
}

impl BufferPool {
    fn new(max: usize, gauge: Arc<AtomicUsize>) -> Arc<Self> {
        Arc::new(Self {
            in_flight: Mutex::new(0),
            cv: Condvar::new(),
            max,
            gauge,
            free: Mutex::new(Vec::new()),
        })
    }

    fn acquire(self: &Arc<Self>, state: &JobState) -> Option<Vec<u8>> {
        let mut count = self.in_flight.lock().unwrap();
        while *count >= self.max {
            if state.cancel.load(Ordering::Relaxed) { return None; }
            let (next, _) = self.cv.wait_timeout(count, Duration::from_millis(80)).unwrap();
            count = next;
        }
        if state.cancel.load(Ordering::Relaxed) { return None; }
        *count += 1;
        self.gauge.store(*count, Ordering::Relaxed);
        drop(count);

        let mut buf = self.free.lock().unwrap().pop().unwrap_or_else(|| Vec::with_capacity(BLOCK));
        buf.resize(BLOCK, 0);
        Some(buf)
    }

    fn release(&self, mut data: Vec<u8>) {
        data.clear();
        {
            let mut free = self.free.lock().unwrap();
            if free.len() < MAX_FREE_BUFFERS && data.capacity() >= BLOCK {
                free.push(data);
            }
        }
        let mut count = self.in_flight.lock().unwrap();
        *count = count.saturating_sub(1);
        self.gauge.store(*count, Ordering::Relaxed);
        self.cv.notify_one();
    }
}

struct Buffer {
    data: Vec<u8>,
    pool: Arc<BufferPool>,
}

type BufferRef = Arc<Buffer>;

impl Drop for Buffer {
    fn drop(&mut self) {
        let data = std::mem::take(&mut self.data);
        self.pool.release(data);
    }
}

enum FanoutItem {
    Begin(FileInfo),
    Data(BufferRef),
    End { hash: [u8; 32] },
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum OperationPhase {
    Write,
    Sync,
    Verify,
    Commit,
}

struct DestControl {
    alive: AtomicBool,
    queue_depth: AtomicUsize,
    progress_seq: AtomicU64,
    operation: Mutex<(OperationPhase, Instant)>,
}

impl DestControl {
    fn new() -> Self {
        Self {
            alive: AtomicBool::new(true),
            queue_depth: AtomicUsize::new(0),
            progress_seq: AtomicU64::new(0),
            operation: Mutex::new((OperationPhase::Write, Instant::now())),
        }
    }

    fn note_progress(&self) {
        self.progress_seq.fetch_add(1, Ordering::AcqRel);
    }

    fn progress_seq(&self) -> u64 {
        self.progress_seq.load(Ordering::Acquire)
    }

    fn enter_operation(&self, phase: OperationPhase) {
        *self.operation.lock().unwrap() = (phase, Instant::now());
        if phase == OperationPhase::Write {
            self.note_progress();
        }
    }

    fn stall_timed_out(&self, last_write_progress: Instant) -> bool {
        let (phase, started) = *self.operation.lock().unwrap();
        match phase {
            OperationPhase::Write => last_write_progress.elapsed() >= STALL_THRESHOLD,
            OperationPhase::Sync | OperationPhase::Verify | OperationPhase::Commit => {
                started.elapsed() >= LONG_OP_THRESHOLD
            }
        }
    }

    fn stall_limit_secs(&self) -> u64 {
        let (phase, _) = *self.operation.lock().unwrap();
        match phase {
            OperationPhase::Write => STALL_THRESHOLD.as_secs(),
            OperationPhase::Sync | OperationPhase::Verify | OperationPhase::Commit => {
                LONG_OP_THRESHOLD.as_secs()
            }
        }
    }
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
    UNIX_EPOCH.checked_add(Duration::new(
        secs as u64,
        (info.mtime_ns % 1_000_000_000) as u32,
    ))
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
        if entry.depth() == 0 { continue; }
        let ft = entry.file_type();
        if ft.is_symlink() {
            return Err(format!("No se puede copiar con seguridad el enlace simbólico {}.", entry.path().display()));
        }
        if ft.is_dir() {
            dirs.push(entry.path().strip_prefix(root).map_err(|e| e.to_string())?.to_path_buf());
        }
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
            fs::create_dir_all(&path)
                .map_err(|e| format!("No se pudo crear la carpeta {}: {e}", path.display()))?;
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

fn record_file_error(state: &JobState, slot: usize, msg: String) {
    let mut g = state.dests.lock().unwrap();
    g[slot].files_err = g[slot].files_err.saturating_add(1);
    g[slot].error = Some(msg);
}

fn same_enough(src: &Path, dst: &Path) -> bool {
    let (Ok(a), Ok(b)) = (fs::metadata(src), fs::metadata(dst)) else { return false; };
    if !a.is_file() || !b.is_file() || a.len() != b.len() { return false; }
    match (a.modified(), b.modified()) {
        (Ok(x), Ok(y)) => x == y,
        _ => false,
    }
}

fn state_id(dest: &Path) -> String {
    let hash = blake3::hash(dest.to_string_lossy().as_bytes()).to_hex().to_string();
    hash[..16].to_owned()
}

pub(crate) fn state_dir_for(dest: &Path) -> PathBuf {
    let parent = dest.parent().unwrap_or(dest);
    parent.join(".disk-duplicator-state").join(state_id(dest))
}

fn state_path(dest: &Path) -> PathBuf {
    state_dir_for(dest).join("completed.jsonl")
}

fn manifest_path(dest: &Path) -> PathBuf {
    state_dir_for(dest).join("manifest.b3")
}

fn load_state(dest: &Path) -> HashSet<String> {
    let Ok(text) = fs::read_to_string(state_path(dest)) else { return HashSet::new(); };
    text.lines().filter_map(|line| {
        let (_, rest) = line.split_once("\"key\":\"")?;
        let (key, _) = rest.split_once('"')?;
        Some(key.to_owned())
    }).collect()
}

struct StateJournal {
    writer: BufWriter<File>,
    pending: usize,
    last_sync: Instant,
}

impl StateJournal {
    fn open(dest: &Path) -> Result<Self, String> {
        let dir = state_dir_for(dest);
        fs::create_dir_all(&dir).map_err(|e| format!("state mkdir {}: {e}", dir.display()))?;
        let file = OpenOptions::new().create(true).append(true).open(state_path(dest))
            .map_err(|e| format!("state open: {e}"))?;
        Ok(Self {
            writer: BufWriter::with_capacity(64 * 1024, file),
            pending: 0,
            last_sync: Instant::now(),
        })
    }

    fn append(&mut self, key: &str) -> Result<(), String> {
        writeln!(self.writer, "{{\"key\":\"{key}\"}}")
            .map_err(|e| format!("state write: {e}"))?;
        self.pending += 1;
        if self.pending >= STATE_BATCH_FILES || self.last_sync.elapsed() >= STATE_BATCH_INTERVAL {
            self.checkpoint()?;
        }
        Ok(())
    }

    fn checkpoint(&mut self) -> Result<(), String> {
        if self.pending == 0 { return Ok(()); }
        self.writer.flush().map_err(|e| format!("state flush: {e}"))?;
        self.writer.get_ref().sync_data().map_err(|e| format!("state sync: {e}"))?;
        self.pending = 0;
        self.last_sync = Instant::now();
        Ok(())
    }

    fn finish(&mut self) -> Result<(), String> { self.checkpoint() }
}

impl Drop for StateJournal {
    fn drop(&mut self) { let _ = self.checkpoint(); }
}

struct ManifestWriter {
    writer: BufWriter<File>,
    dirty: bool,
}

impl ManifestWriter {
    fn open(dest: &Path) -> Result<Self, String> {
        let dir = state_dir_for(dest);
        fs::create_dir_all(&dir).map_err(|e| format!("manifest mkdir {}: {e}", dir.display()))?;
        let file = OpenOptions::new().create(true).append(true).open(manifest_path(dest))
            .map_err(|e| format!("manifest open: {e}"))?;
        Ok(Self { writer: BufWriter::with_capacity(64 * 1024, file), dirty: false })
    }

    fn append(&mut self, rel: &Path, hash: &blake3::Hash) -> Result<(), String> {
        let name = rel.to_string_lossy().replace('\\', "/");
        writeln!(self.writer, "{}  {name}", hash.to_hex())
            .map_err(|e| format!("manifest write: {e}"))?;
        self.dirty = true;
        Ok(())
    }

    fn finish(&mut self) -> Result<(), String> {
        if !self.dirty { return Ok(()); }
        self.writer.flush().map_err(|e| format!("manifest flush: {e}"))?;
        self.writer.get_ref().sync_data().map_err(|e| format!("manifest sync: {e}"))?;
        self.dirty = false;
        Ok(())
    }
}

impl Drop for ManifestWriter {
    fn drop(&mut self) { let _ = self.finish(); }
}

fn transient_id(path: &Path) -> String {
    let hash = blake3::hash(path.to_string_lossy().as_bytes()).to_hex().to_string();
    hash[..24].to_owned()
}

fn part_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root).join("tmp").join(format!("{}.part", transient_id(dst)))
}

fn backup_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root).join("tmp").join(format!("{}.bak", transient_id(dst)))
}

fn cleanup_part(dest_root: &Path, dst: &Path) {
    let _ = fs::remove_file(part_path(dest_root, dst));
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
    f.set_modified(mtime).map_err(|e| format!("metadata: {e}"))
}

fn record_write_progress(
    state: &JobState,
    slot: usize,
    size: u64,
    effective_written: &mut u64,
    start: Instant,
) {
    *effective_written = effective_written.saturating_add(size);
    let mut g = state.dests.lock().unwrap();
    g[slot].written = g[slot].written.saturating_add(size);
    let secs = start.elapsed().as_secs_f64();
    if secs > 0.0 { g[slot].bps = *effective_written as f64 / secs; }
}

fn rollback_write_progress(
    state: &JobState,
    slot: usize,
    size: u64,
    effective_written: &mut u64,
    start: Instant,
) {
    *effective_written = effective_written.saturating_sub(size);
    let mut g = state.dests.lock().unwrap();
    g[slot].written = g[slot].written.saturating_sub(size);
    let secs = start.elapsed().as_secs_f64();
    g[slot].bps = if secs > 0.0 { *effective_written as f64 / secs } else { 0.0 };
}

fn record_done(state: &JobState, slot: usize) {
    state.dests.lock().unwrap()[slot].files_done += 1;
}

fn finish_logs(manifest: &mut ManifestWriter, journal: &mut StateJournal) -> Result<(), String> {
    manifest.finish().and(journal.finish())
}
