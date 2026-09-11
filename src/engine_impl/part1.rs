use crate::paths::{backup_path, manifest_path, part_path, persisted_path_key, state_dir_for, state_path};
use crossbeam_channel as mpsc;
use std::collections::{HashMap, HashSet};
use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Write};
#[cfg(not(windows))]
use std::io::Read;
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const BLOCK: usize = 16 * 1024 * 1024;
const RESERVED_RAM: usize = 512 * 1024 * 1024;
const MIN_QUEUE: usize = 2;
const MAX_QUEUE: usize = 16;
const MAX_FREE_BUFFERS: usize = 16;
const RETRIES: usize = 2;
const WRITE_STALL_THRESHOLD: Duration = Duration::from_secs(30);
const LONG_OP_THRESHOLD: Duration = Duration::from_secs(120);
const STATE_BATCH_FILES: usize = 128;
const STATE_BATCH_INTERVAL: Duration = Duration::from_secs(1);
const SPEED_TAU_SECS: f64 = 2.0;

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

#[derive(Clone)]
pub struct DestProgress {
    pub label: String,
    pub written: u64,
    pub total: u64,
    pub files_done: u64,
    pub files_skip: u64,
    pub files_err: u64,
    pub bps: f64,
    pub bps_recent: f64,
    pub last_tick: Instant,
    pub phase: DestPhase,
    pub error: Option<String>,
    pub last_file: String,
    pub queue_depth: usize,
    pub retries: u64,
}

pub struct JobState {
    pub running: AtomicBool,
    pub cancel: AtomicBool,
    pub pause: AtomicBool,
    pub(crate) pause_mutex: Mutex<()>,
    pub(crate) pause_cv: Condvar,
    pub files_total: AtomicU64,
    pub bytes_total: AtomicU64,
    pub buffers_in_flight: Arc<AtomicUsize>,
    pub dests: Mutex<Vec<DestProgress>>,
    pub(crate) reader_hashes: Mutex<HashMap<PathBuf, [u8; 32]>>,
}

impl JobState {
    pub fn snapshot(&self) -> Vec<DestProgress> {
        self.dests.lock().unwrap().clone()
    }

    pub fn is_paused(&self) -> bool {
        self.pause.load(Ordering::Acquire)
    }

    pub fn set_paused(&self, paused: bool) {
        {
            let _guard = self.pause_mutex.lock().unwrap();
            self.pause.store(paused, Ordering::Release);
        }
        if !paused {
            self.pause_cv.notify_all();
        }
    }

    pub fn request_cancel(&self) {
        {
            let _guard = self.pause_mutex.lock().unwrap();
            self.cancel.store(true, Ordering::Release);
            self.pause.store(false, Ordering::Release);
        }
        self.pause_cv.notify_all();
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

    fn take_buffer(&self) -> Vec<u8> {
        let buf = self
            .free
            .lock()
            .unwrap()
            .pop()
            .unwrap_or_else(|| vec![0u8; BLOCK]);
        debug_assert_eq!(buf.len(), BLOCK);
        buf
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
        Some(self.take_buffer())
    }

    fn try_acquire(&self, state: &JobState) -> Option<Vec<u8>> {
        if state.cancel.load(Ordering::Relaxed) { return None; }
        let mut count = self.in_flight.lock().unwrap();
        if *count >= self.max { return None; }
        *count += 1;
        self.gauge.store(*count, Ordering::Relaxed);
        drop(count);
        Some(self.take_buffer())
    }

    fn release(&self, data: Vec<u8>) {
        {
            let mut free = self.free.lock().unwrap();
            if free.len() < MAX_FREE_BUFFERS && data.len() == BLOCK {
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
    len: usize,
    pool: Arc<BufferPool>,
}

impl Buffer {
    fn bytes(&self) -> &[u8] {
        &self.data[..self.len]
    }
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
            OperationPhase::Write => last_write_progress.elapsed() >= WRITE_STALL_THRESHOLD,
            OperationPhase::Sync | OperationPhase::Verify | OperationPhase::Commit => {
                started.elapsed() >= LONG_OP_THRESHOLD
            }
        }
    }

    fn stall_limit_secs(&self) -> u64 {
        let (phase, _) = *self.operation.lock().unwrap();
        match phase {
            OperationPhase::Write => WRITE_STALL_THRESHOLD.as_secs(),
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

fn create_directory_layout(dests: &[PathBuf], dirs: &[PathBuf]) -> Result<(), String> {
    for dest in dests {
        fs::create_dir_all(dest).map_err(|e| format!("destino {}: {e}", dest.display()))?;
        for rel in dirs {
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

#[cfg(windows)]
fn runtime_is_reparse(meta: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    meta.file_attributes() & 0x0000_0400 != 0
}

#[cfg(not(windows))]
fn runtime_is_reparse(_: &fs::Metadata) -> bool { false }

fn validate_runtime_destination_path(root: &Path, rel: &Path) -> Result<(), String> {
    let root_meta = fs::symlink_metadata(root)
        .map_err(|e| format!("No se pudo inspeccionar destino {}: {e}", root.display()))?;
    if !root_meta.is_dir() || root_meta.file_type().is_symlink() || runtime_is_reparse(&root_meta) {
        return Err(format!("Destino inseguro o reemplazado durante la copia: {}", root.display()));
    }
    let components: Vec<Component<'_>> = rel.components().collect();
    if components.is_empty() { return Err("Ruta relativa vacía".into()); }
    let mut current = root.to_path_buf();
    for (idx, component) in components.iter().enumerate() {
        let Component::Normal(name) = component else {
            return Err(format!("Ruta relativa insegura: {}", rel.display()));
        };
        current.push(name);
        match fs::symlink_metadata(&current) {
            Ok(meta) => {
                if meta.file_type().is_symlink() || runtime_is_reparse(&meta) {
                    return Err(format!("La ruta de destino cambió a symlink/junction/reparse point durante la copia: {}", current.display()));
                }
                let last = idx + 1 == components.len();
                if !last && !meta.is_dir() {
                    return Err(format!("Componente de destino ya no es carpeta: {}", current.display()));
                }
                if last && meta.is_dir() {
                    return Err(format!("El destino final cambió a carpeta: {}", current.display()));
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(format!("No se pudo inspeccionar {}: {e}", current.display())),
        }
    }
    Ok(())
}

fn wait_pause(state: &JobState) -> bool {
    let mut guard = state.pause_mutex.lock().unwrap();
    while state.pause.load(Ordering::Acquire) && !state.cancel.load(Ordering::Acquire) {
        guard = state.pause_cv.wait(guard).unwrap();
    }
    !state.cancel.load(Ordering::Acquire)
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

fn load_state(dest: &Path) -> HashSet<String> {
    let Ok(text) = fs::read_to_string(state_path(dest)) else { return HashSet::new(); };
    text.lines().filter_map(|line| {
        let (_, rest) = line.split_once("\"key\":\"")?;
        let (key, _) = rest.split_once('"')?;
        Some(key.to_owned())
    }).collect()
}

fn sync_log_data(file: &File, state: &JobState, control: &DestControl, label: &str) -> Result<(), String> {
    #[cfg(windows)]
    {
        let clone = file.try_clone().map_err(|e| format!("{label} sync clone: {e}"))?;
        crate::windows_io::sync_file_cancelable(clone, || {
            state.cancel.load(Ordering::Acquire) || !control.alive.load(Ordering::Acquire)
        }).map_err(|e| {
            if e.kind() == std::io::ErrorKind::Interrupted && state.cancel.load(Ordering::Acquire) {
                "Cancelado".to_owned()
            } else if e.kind() == std::io::ErrorKind::Interrupted && !control.alive.load(Ordering::Acquire) {
                format!("{label} interrumpido porque el destino dejó de responder")
            } else {
                format!("{label} sync: {e}")
            }
        })
    }
    #[cfg(not(windows))]
    {
        file.sync_data().map_err(|e| format!("{label} sync: {e}"))
    }
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
        Ok(())
    }

    fn needs_checkpoint(&self) -> bool {
        self.pending >= STATE_BATCH_FILES || self.last_sync.elapsed() >= STATE_BATCH_INTERVAL
    }

    fn checkpoint(&mut self, state: &JobState, control: &DestControl) -> Result<(), String> {
        if self.pending == 0 { return Ok(()); }
        self.writer.flush().map_err(|e| format!("state flush: {e}"))?;
        sync_log_data(self.writer.get_ref(), state, control, "state")?;
        self.pending = 0;
        self.last_sync = Instant::now();
        Ok(())
    }

    fn finish(&mut self, state: &JobState, control: &DestControl) -> Result<(), String> {
        self.checkpoint(state, control)
    }
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
        let key = persisted_path_key(rel);
        writeln!(self.writer, "{}  {key}", hash.to_hex())
            .map_err(|e| format!("manifest write: {e}"))?;
        self.dirty = true;
        Ok(())
    }

    fn finish(&mut self, state: &JobState, control: &DestControl) -> Result<(), String> {
        if !self.dirty { return Ok(()); }
        self.writer.flush().map_err(|e| format!("manifest flush: {e}"))?;
        sync_log_data(self.writer.get_ref(), state, control, "manifest")?;
        self.dirty = false;
        Ok(())
    }
}

impl Drop for ManifestWriter {
    fn drop(&mut self) { let _ = self.writer.flush(); }
}

fn cleanup_part(dest_root: &Path, dst: &Path) {
    let path = part_path(dest_root, dst);
    match fs::remove_file(&path) {
        Ok(()) => {}
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(e) => eprintln!("Advertencia: no se pudo eliminar {}: {e}", path.display()),
    }
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
    let dp = &mut g[slot];
    dp.written = dp.written.saturating_add(size);

    let elapsed = start.elapsed().as_secs_f64();
    if elapsed > 0.0 {
        dp.bps = *effective_written as f64 / elapsed;
    }

    let now = Instant::now();
    let dt = now.duration_since(dp.last_tick).as_secs_f64();
    if dt > 0.0 {
        let instant_bps = size as f64 / dt;
        let alpha = if dt >= SPEED_TAU_SECS {
            1.0
        } else {
            1.0 - (-dt / SPEED_TAU_SECS).exp()
        };
        dp.bps_recent += alpha * (instant_bps - dp.bps_recent);
        dp.last_tick = now;
    }
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

fn checkpoint_logs(manifest: &mut ManifestWriter, journal: &mut StateJournal, state: &JobState, control: &DestControl) -> Result<(), String> {
    manifest.finish(state, control)?;
    journal.checkpoint(state, control)
}

fn finish_logs(manifest: &mut ManifestWriter, journal: &mut StateJournal, state: &JobState, control: &DestControl) -> Result<(), String> {
    manifest.finish(state, control)?;
    journal.finish(state, control)
}
