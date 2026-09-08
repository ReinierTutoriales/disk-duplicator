use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufRead, BufReader, BufWriter, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use walkdir::WalkDir;
use crossbeam_channel as mpsc;

const BLOCK: usize = 4 * 1024 * 1024;
const RESERVED_RAM: usize = 512 * 1024 * 1024;
const MIN_QUEUE: usize = 2;
const MAX_QUEUE: usize = 64;
const RETRIES: usize = 3;
const SEND_POLL: Duration = Duration::from_millis(150);
const DELIVERY_RETRY_SLEEP: Duration = Duration::from_millis(2);
const STALL_THRESHOLD: Duration = Duration::from_secs(6);
const STATE_BATCH_FILES: usize = 128;
const STATE_BATCH_INTERVAL: Duration = Duration::from_secs(1);
const RECONNECT_WINDOW: Duration = Duration::from_secs(8);
const SPACE_CHECK_INTERVAL: u64 = 256 * 1024 * 1024;
const RUNTIME_SPACE_RESERVE: u64 = 256 * 1024 * 1024;

#[derive(Clone, Copy)]
pub struct CopyOpts { pub verify: bool, pub skip_same: bool, pub keep_going: bool }

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum DestPhase { Idle, Copying, Verifying, Done, Failed, Cancelled }

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum CopyMode { Fanout, PerDestination, Fallback }

#[derive(Clone)]
pub struct DestProgress {
    pub label: String, pub written: u64, pub total: u64, pub files_done: u64,
    pub files_skip: u64, pub files_err: u64, pub bps: f64, pub phase: DestPhase,
    pub error: Option<String>, pub last_file: String, pub mode: CopyMode,
    pub queue_depth: usize, pub retries: u64,
}

pub struct JobState {
    pub running: AtomicBool, pub cancel: AtomicBool, pub pause: AtomicBool,
    pub files_total: AtomicU64, pub bytes_total: AtomicU64,
    pub buffers_in_flight: Arc<AtomicUsize>, pub max_buffers: usize,
    pub fanout: bool, pub dests: Mutex<Vec<DestProgress>>,
}
impl JobState { pub fn snapshot(&self) -> Vec<DestProgress> { self.dests.lock().unwrap().clone() } }

struct BufferBudget { in_flight: Mutex<usize>, cv: Condvar, max: usize, gauge: Arc<AtomicUsize> }
impl BufferBudget {
    fn new(max: usize, gauge: Arc<AtomicUsize>) -> Arc<Self> { Arc::new(Self { in_flight: Mutex::new(0), cv: Condvar::new(), max, gauge }) }
    fn acquire(&self, state: &JobState) -> bool {
        let mut n = self.in_flight.lock().unwrap();
        while *n >= self.max {
            if state.cancel.load(Ordering::Relaxed) { return false; }
            let (next, _) = self.cv.wait_timeout(n, Duration::from_millis(80)).unwrap(); n = next;
        }
        if state.cancel.load(Ordering::Relaxed) { return false; }
        *n += 1; self.gauge.store(*n, Ordering::Relaxed); true
    }
    fn release(&self) {
        let mut n = self.in_flight.lock().unwrap(); *n = n.saturating_sub(1);
        self.gauge.store(*n, Ordering::Relaxed); self.cv.notify_one();
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
struct DestControl { alive: AtomicBool, queue_depth: AtomicUsize, last_progress: Mutex<Instant> }
impl DestControl {
    fn new() -> Self {
        Self { alive: AtomicBool::new(true), queue_depth: AtomicUsize::new(0), last_progress: Mutex::new(Instant::now()) }
    }
    fn mark_progress(&self) { *self.last_progress.lock().unwrap() = Instant::now(); }
    fn stalled_for(&self) -> Duration { self.last_progress.lock().unwrap().elapsed() }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum IoClass { Transient, Disconnected, NoSpace, Permission, Permanent }

fn queue_depth_for(n_dests: usize) -> usize {
    if n_dests == 0 { return MIN_QUEUE; }
    (RESERVED_RAM / (n_dests * BLOCK)).clamp(MIN_QUEUE, MAX_QUEUE)
}

fn metadata_mtime_ns(meta: &fs::Metadata) -> u128 {
    meta.modified().ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_nanos())
        .unwrap_or(0)
}

fn expected_mtime(info: &FileInfo) -> Option<SystemTime> {
    if info.mtime_ns == 0 { return None; }
    let secs = info.mtime_ns / 1_000_000_000;
    if secs > u64::MAX as u128 { return None; }
    let nanos = (info.mtime_ns % 1_000_000_000) as u32;
    UNIX_EPOCH.checked_add(Duration::new(secs as u64, nanos))
}

fn validate_source_snapshot(path: &Path, info: &FileInfo) -> Result<(), String> {
    let meta = fs::metadata(path).map_err(|e| format!("[source] origen {}: {e}", path.display()))?;
    if !meta.is_file() || meta.len() != info.size || metadata_mtime_ns(&meta) != info.mtime_ns {
        return Err(format!("[source] origen cambió: {}", path.display()));
    }
    Ok(())
}

fn list_files(root: &Path) -> Result<Vec<FileInfo>, String> {
    let mut out = Vec::new();
    for entry in WalkDir::new(root).follow_links(false) {
        let entry = entry.map_err(|e| e.to_string())?;
        if !entry.file_type().is_file() { continue; }
        let name = entry.file_name().to_string_lossy();
        if name.ends_with(".part") || name == "paquetecopies.b3" { continue; }
        let meta = entry.metadata().map_err(|e| e.to_string())?;
        let rel = entry.path().strip_prefix(root).map_err(|e| e.to_string())?.to_path_buf();
        out.push(FileInfo { rel, size: meta.len(), mtime_ns: metadata_mtime_ns(&meta) });
    }
    out.sort_unstable_by(|a, b| a.rel.cmp(&b.rel));
    out.shrink_to_fit();
    Ok(out)
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
    let mut g = state.dests.lock().unwrap(); g[slot].phase = phase; if err.is_some() { g[slot].error = err; }
}

fn set_error(state: &JobState, slot: usize, msg: String) { state.dests.lock().unwrap()[slot].error = Some(msg); }

fn same_enough(src: &Path, dst: &Path) -> bool {
    let Ok(a) = fs::metadata(src) else { return false; };
    let Ok(b) = fs::metadata(dst) else { return false; };
    if !a.is_file() || !b.is_file() || a.len() != b.len() { return false; }
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

fn state_id_for_key(key: &str) -> u128 {
    let hash = blake3::hash(key.as_bytes());
    let mut bytes = [0u8; 16];
    bytes.copy_from_slice(&hash.as_bytes()[..16]);
    u128::from_le_bytes(bytes)
}

fn state_id(info: &FileInfo) -> u128 { state_id_for_key(&state_key(info)) }

fn load_state(dest: &Path) -> HashSet<u128> {
    let Ok(file) = File::open(state_path(dest)) else { return HashSet::new(); };
    let reader = BufReader::with_capacity(64 * 1024, file);
    reader.lines().filter_map(Result::ok).filter_map(|line| {
        let (_, rest) = line.split_once("\"key\":\"")?;
        let (key, _) = rest.split_once('\"')?;
        Some(state_id_for_key(key))
    }).collect()
}

struct StateJournal {
    writer: BufWriter<File>,
    pending: usize,
    last_sync: Instant,
}
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
        self.pending = 0; self.last_sync = Instant::now(); Ok(())
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
        let name = rel.to_string_lossy().replace("\\", "/");
        writeln!(self.writer, "{}  {name}", hash.to_hex()).map_err(|e| format!("manifiesto: {e}"))?;
        self.dirty = true; Ok(())
    }
    fn finish(&mut self) -> Result<(), String> {
        if !self.dirty { return Ok(()); }
        self.writer.flush().map_err(|e| format!("manifiesto flush: {e}"))?;
        self.writer.get_ref().sync_all().map_err(|e| format!("manifiesto sync: {e}"))?;
        self.dirty = false; Ok(())
    }
}
impl Drop for ManifestWriter { fn drop(&mut self) { let _ = self.finish(); } }

fn part_path(dst: &Path) -> PathBuf { let mut p = dst.as_os_str().to_os_string(); p.push(".part"); PathBuf::from(p) }
fn cleanup_part(dst: &Path) { let _ = fs::remove_file(part_path(dst)); }

fn classify_io_error(err: &io::Error) -> IoClass {
    if matches!(err.raw_os_error(), Some(28) | Some(39) | Some(112) | Some(122)) { return IoClass::NoSpace; }
    match err.kind() {
        io::ErrorKind::Interrupted | io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut => IoClass::Transient,
        io::ErrorKind::NotFound | io::ErrorKind::BrokenPipe | io::ErrorKind::ConnectionAborted |
        io::ErrorKind::ConnectionReset | io::ErrorKind::NotConnected | io::ErrorKind::UnexpectedEof => IoClass::Disconnected,
        io::ErrorKind::PermissionDenied => IoClass::Permission,
        _ => IoClass::Permanent,
    }
}

fn class_tag(class: IoClass) -> &'static str {
    match class {
        IoClass::Transient => "transient",
        IoClass::Disconnected => "disconnected",
        IoClass::NoSpace => "space",
        IoClass::Permission => "permission",
        IoClass::Permanent => "io",
    }
}

fn retry_delay(attempt: usize) -> Duration {
    Duration::from_millis((100u64.saturating_mul(1u64 << attempt.min(3))).min(800))
}

fn retry_io<T>(state: &JobState, slot: usize, context: &str, mut op: impl FnMut() -> io::Result<T>) -> Result<T, String> {
    let started = Instant::now();
    let mut attempt = 0usize;
    loop {
        match op() {
            Ok(value) => return Ok(value),
            Err(err) => {
                let class = classify_io_error(&err);
                let retry = match class {
                    IoClass::Transient => attempt < RETRIES,
                    IoClass::Disconnected => started.elapsed() < RECONNECT_WINDOW,
                    IoClass::NoSpace | IoClass::Permission | IoClass::Permanent => false,
                };
                if !retry { return Err(format!("[{}] {context}: {err}", class_tag(class))); }
                state.dests.lock().unwrap()[slot].retries += 1;
                thread::sleep(retry_delay(attempt));
                if !wait_pause(state) { return Err("Cancelado".into()); }
                attempt = attempt.saturating_add(1);
            }
        }
    }
}

fn is_terminal_destination_error(msg: &str) -> bool {
    msg.starts_with("[space]") || msg.starts_with("[permission]") || msg.starts_with("[unsafe]")
}

fn existing_anchor(path: &Path) -> Option<&Path> { path.ancestors().find(|p| p.exists()) }

fn ensure_runtime_space(path: &Path, remaining: u64) -> Result<(), String> {
    let Some(anchor) = existing_anchor(path) else {
        return Err(format!("[disconnected] destino no disponible: {}", path.display()));
    };
    let available = fs2::available_space(anchor)
        .map_err(|e| format!("[io] espacio libre {}: {e}", anchor.display()))?;
    let required = remaining.saturating_add(RUNTIME_SPACE_RESERVE);
    if available < required {
        return Err(format!(
            "[space] espacio insuficiente durante la copia en {}: disponibles {} bytes, requeridos {} bytes (incluye reserva).",
            anchor.display(), available, required
        ));
    }
    Ok(())
}

fn validate_destination_layout_runtime(dest: &Path, rel: &Path) -> Result<(), String> {
    let components: Vec<Component<'_>> = rel.components().collect();
    if components.is_empty() { return Err("[unsafe] ruta relativa vacía.".into()); }
    let count = components.len();
    let mut current = dest.to_path_buf();
    for (index, component) in components.into_iter().enumerate() {
        match component {
            Component::Normal(name) => current.push(name),
            _ => return Err(format!("[unsafe] ruta relativa no segura: {}", rel.display())),
        }
        let meta = match fs::symlink_metadata(&current) {
            Ok(meta) => meta,
            Err(e) if e.kind() == io::ErrorKind::NotFound => continue,
            Err(e) => return Err(format!("[io] inspección {}: {e}", current.display())),
        };
        if meta.file_type().is_symlink() {
            return Err(format!("[unsafe] enlace simbólico detectado durante la copia: {}", current.display()));
        }
        let last = index + 1 == count;
        if last && meta.is_dir() {
            return Err(format!("[unsafe] el destino cambió a carpeta: {}", current.display()));
        }
        if !last && !meta.is_dir() {
            return Err(format!("[unsafe] un padre del destino cambió a archivo: {}", current.display()));
        }
    }
    Ok(())
}

fn hash_file(path: &Path, state: Option<&JobState>) -> Result<blake3::Hash, String> {
    let mut f = File::open(path).map_err(|e| format!("verificar: {e}"))?;
    let mut buf = vec![0u8; BLOCK]; let mut h = blake3::Hasher::new();
    loop {
        if let Some(s) = state { if !wait_pause(s) { return Err("Cancelado".into()); } }
        let n = f.read(&mut buf).map_err(|e| format!("verificar: {e}"))?;
        if n == 0 { break; } h.update(&buf[..n]);
    }
    Ok(h.finalize())
}

fn set_mtime(dst: &Path, mtime: SystemTime, state: &JobState, slot: usize) -> Result<(), String> {
    let f = retry_io(state, slot, "metadata open", || OpenOptions::new().write(true).open(dst))?;
    retry_io(state, slot, "metadata", || f.set_modified(mtime))?;
    retry_io(state, slot, "metadata sync", || f.sync_all())
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

fn copy_file_atomic(src: &Path, dst: &Path, dest_root: &Path, info: &FileInfo, verify: bool, state: &JobState, slot: usize, effective_written: &mut u64, start: Instant) -> Result<blake3::Hash, String> {
    validate_destination_layout_runtime(dest_root, &info.rel)?;
    if let Some(parent) = dst.parent() { retry_io(state, slot, "mkdir", || fs::create_dir_all(parent))?; }
    ensure_runtime_space(dst, info.size)?;
    validate_source_snapshot(src, info)?;
    let mut input = File::open(src).map_err(|e| format!("[source] abrir origen: {e}"))?;
    let tmp = part_path(dst); cleanup_part(dst);
    let mut copied = 0u64;
    let result = (|| {
        let mut output = retry_io(state, slot, "crear .part", || File::create(&tmp))?;
        let mut buf = vec![0u8; BLOCK]; let mut h = blake3::Hasher::new(); let mut since_space_check = 0u64;
        loop {
            if !wait_pause(state) { return Err("Cancelado".into()); }
            let n = input.read(&mut buf).map_err(|e| format!("[source] lectura: {e}"))?;
            if n == 0 { break; }
            retry_io(state, slot, "escritura", || output.write_all(&buf[..n]))?;
            copied += n as u64; since_space_check += n as u64;
            record_write_progress(state, slot, n as u64, effective_written, start);
            h.update(&buf[..n]);
            if since_space_check >= SPACE_CHECK_INTERVAL {
                ensure_runtime_space(dst, info.size.saturating_sub(copied))?;
                since_space_check = 0;
            }
        }
        if copied != info.size { return Err(format!("[source] origen cambió: {}", src.display())); }
        validate_source_snapshot(src, info)?;
        retry_io(state, slot, "flush/sync", || output.flush().and_then(|_| output.sync_all()))?; drop(output);
        let expected = h.finalize();
        if verify {
            set_phase(state, slot, DestPhase::Verifying, None);
            if hash_file(&tmp, Some(state))? != expected { return Err(format!("BLAKE3 no coincide: {}", src.display())); }
            set_phase(state, slot, DestPhase::Copying, None);
        }
        validate_destination_layout_runtime(dest_root, &info.rel)?;
        retry_io(state, slot, "commit", || fs::rename(&tmp, dst))?;
        if let Some(m) = expected_mtime(info) { set_mtime(dst, m, state, slot)?; }
        Ok(expected)
    })();
    if result.is_err() {
        cleanup_part(dst);
        rollback_write_progress(state, slot, copied, effective_written, start);
    }
    result
}

fn record_skip(state: &JobState, slot: usize, size: u64) {
    let mut g = state.dests.lock().unwrap(); g[slot].files_skip += 1; g[slot].files_done += 1; g[slot].written += size;
}
fn record_done(state: &JobState, slot: usize) { state.dests.lock().unwrap()[slot].files_done += 1; }

fn finish_logs(manifest: &mut ManifestWriter, journal: &mut StateJournal) -> Result<(), String> {
    let manifest_result = manifest.finish();
    let journal_result = journal.finish();
    manifest_result.and(journal_result)
}

fn copy_one_with_retries(src: &Path, dst: &Path, dest_root: &Path, info: &FileInfo, opts: CopyOpts, state: &JobState, slot: usize, effective_written: &mut u64, start: Instant, manifest: &mut ManifestWriter) -> bool {
    for attempt in 0..=RETRIES {
        if state.cancel.load(Ordering::Relaxed) { return false; }
        match copy_file_atomic(src, dst, dest_root, info, opts.verify, state, slot, effective_written, start) {
            Ok(hash) => {
                if let Err(e) = manifest.append(&info.rel, &hash) { set_error(state, slot, e); return false; }
                return true;
            }
            Err(e) if e == "Cancelado" => return false,
            Err(e) if e.starts_with("[disconnected]") && attempt < RETRIES => {
                set_error(state, slot, format!("{e} · esperando reconexión"));
                state.dests.lock().unwrap()[slot].retries += 1;
                let until = Instant::now() + RECONNECT_WINDOW;
                while Instant::now() < until {
                    if !wait_pause(state) { return false; }
                    if existing_anchor(dest_root).is_some() { break; }
                    thread::sleep(Duration::from_millis(250));
                }
            }
            Err(e) => { set_error(state, slot, e); return false; }
        }
    }
    false
}

fn per_dest_worker(source: PathBuf, dest: PathBuf, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, slot: usize, opts: CopyOpts, mode: CopyMode) {
    let start = Instant::now();
    let mut effective_written = 0u64;
    { let mut g = state.dests.lock().unwrap(); g[slot].mode = mode; g[slot].phase = DestPhase::Copying; }
    let completed = load_state(&dest);
    let mut journal = match StateJournal::open(&dest) { Ok(j) => j, Err(e) => { set_phase(&state, slot, DestPhase::Failed, Some(e)); return; } };
    let mut manifest = match ManifestWriter::open(&dest) { Ok(m) => m, Err(e) => { set_phase(&state, slot, DestPhase::Failed, Some(e)); return; } };
    for info in files.iter() {
        if !wait_pause(&state) {
            let _ = finish_logs(&mut manifest, &mut journal);
            set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return;
        }
        let key = state_key(info); let key_id = state_id_for_key(&key); let src = source.join(&info.rel); let dst = dest.join(&info.rel);
        state.dests.lock().unwrap()[slot].last_file = info.rel.to_string_lossy().into_owned();
        if completed.contains(&key_id) || (opts.skip_same && same_enough(&src, &dst)) { record_skip(&state, slot, info.size); continue; }
        if !copy_one_with_retries(&src, &dst, &dest, info, opts, &state, slot, &mut effective_written, start, &mut manifest) {
            if state.cancel.load(Ordering::Relaxed) {
                let _ = finish_logs(&mut manifest, &mut journal);
                set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return;
            }
            state.dests.lock().unwrap()[slot].files_err += 1;
            let fatal = state.dests.lock().unwrap()[slot].error.as_deref().is_some_and(is_terminal_destination_error);
            if fatal || !opts.keep_going {
                let _ = finish_logs(&mut manifest, &mut journal);
                set_phase(&state, slot, DestPhase::Failed, None); return;
            }
            continue;
        }
        if let Err(e) = journal.append(&key) {
            set_error(&state, slot, e); state.dests.lock().unwrap()[slot].files_err += 1;
            if !opts.keep_going {
                let _ = finish_logs(&mut manifest, &mut journal);
                set_phase(&state, slot, DestPhase::Failed, None); return;
            }
            continue;
        }
        record_done(&state, slot);
    }
    if let Err(e) = finish_logs(&mut manifest, &mut journal) {
        set_phase(&state, slot, DestPhase::Failed, Some(e)); state.dests.lock().unwrap()[slot].files_err += 1; return;
    }
    let errs = state.dests.lock().unwrap()[slot].files_err;
    if errs == 0 { set_phase(&state, slot, DestPhase::Done, None); }
    else { set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es)."))); }
}

fn drain_queue(rx: &mpsc::Receiver<FanoutItem>) { while rx.try_recv().is_ok() {} }

fn fanout_worker(source: PathBuf, dest: PathBuf, rx: mpsc::Receiver<FanoutItem>, control: Arc<DestControl>, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, slot: usize, opts: CopyOpts, source_failed: Arc<AtomicBool>) {
    let start = Instant::now();
    let mut effective_written = 0u64;
    let mut current: Option<(FileInfo, File, blake3::Hasher, u64, u64)> = None;
    let mut journal = match StateJournal::open(&dest) { Ok(j) => j, Err(e) => { control.alive.store(false, Ordering::Release); set_phase(&state, slot, DestPhase::Failed, Some(e)); return; } };
    let mut manifest = match ManifestWriter::open(&dest) { Ok(m) => m, Err(e) => { control.alive.store(false, Ordering::Release); set_phase(&state, slot, DestPhase::Failed, Some(e)); return; } };
    set_phase(&state, slot, DestPhase::Copying, None);
    loop {
        match rx.recv() {
            Ok(FanoutItem::Begin(info)) => {
                if !control.alive.load(Ordering::Acquire) { continue; }
                state.dests.lock().unwrap()[slot].last_file = info.rel.to_string_lossy().into_owned();
                let dst = dest.join(&info.rel);
                if let Err(e) = validate_destination_layout_runtime(&dest, &info.rel) {
                    control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                }
                if let Some(parent) = dst.parent() {
                    if let Err(e) = retry_io(&state, slot, "mkdir", || fs::create_dir_all(parent)) {
                        control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                    }
                }
                if let Err(e) = ensure_runtime_space(&dst, info.size) {
                    control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                }
                cleanup_part(&dst);
                match retry_io(&state, slot, "crear .part", || File::create(part_path(&dst))) {
                    Ok(f) => current = Some((info, f, blake3::Hasher::new(), 0, 0)),
                    Err(e) => { control.alive.store(false, Ordering::Release); set_error(&state, slot, e); }
                }
            }
            Ok(FanoutItem::Data(buf)) => {
                if let Some((info, f, h, copied, since_space_check)) = current.as_mut() {
                    match retry_io(&state, slot, &format!("escritura {}", info.rel.display()), || f.write_all(&buf.data)) {
                        Ok(()) => {
                            h.update(&buf.data); *copied += buf.data.len() as u64; *since_space_check += buf.data.len() as u64;
                            record_write_progress(&state, slot, buf.data.len() as u64, &mut effective_written, start);
                            if *since_space_check >= SPACE_CHECK_INTERVAL {
                                if let Err(e) = ensure_runtime_space(&dest.join(&info.rel), info.size.saturating_sub(*copied)) {
                                    control.alive.store(false, Ordering::Release); set_error(&state, slot, e);
                                }
                                *since_space_check = 0;
                            }
                        }
                        Err(e) => { control.alive.store(false, Ordering::Release); set_error(&state, slot, e); }
                    }
                }
                control.queue_depth.fetch_sub(1, Ordering::AcqRel);
                state.dests.lock().unwrap()[slot].queue_depth = control.queue_depth.load(Ordering::Acquire);
            }
            Ok(FanoutItem::End { hash }) => {
                let Some((info, mut f, hasher, copied, _)) = current.take() else { continue; };
                let dst = dest.join(&info.rel); let tmp = part_path(&dst);
                if !control.alive.load(Ordering::Acquire) {
                    drop(f); cleanup_part(&dst); rollback_write_progress(&state, slot, copied, &mut effective_written, start); continue;
                }
                if copied != info.size {
                    drop(f); cleanup_part(&dst); rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release);
                    set_error(&state, slot, format!("tamaño inesperado en {}", info.rel.display())); continue;
                }
                if let Err(e) = retry_io(&state, slot, "sync", || f.flush().and_then(|_| f.sync_all())) {
                    drop(f); cleanup_part(&dst); rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                }
                drop(f);
                let expected = hasher.finalize();
                if opts.verify {
                    set_phase(&state, slot, DestPhase::Verifying, None);
                    match hash_file(&tmp, Some(&state)) {
                        Ok(actual) if actual == expected && actual.as_bytes() == &hash => {}
                        Ok(_) => { cleanup_part(&dst); rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release); set_error(&state, slot, format!("BLAKE3 no coincide: {}", dst.display())); continue; }
                        Err(e) => {
                            cleanup_part(&dst); rollback_write_progress(&state, slot, copied, &mut effective_written, start);
                            if e == "Cancelado" { let _ = finish_logs(&mut manifest, &mut journal); set_phase(&state, slot, DestPhase::Cancelled, Some(e)); return; }
                            control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                        }
                    }
                    set_phase(&state, slot, DestPhase::Copying, None);
                }
                if let Err(e) = validate_destination_layout_runtime(&dest, &info.rel) {
                    cleanup_part(&dst); rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                }
                if let Err(e) = retry_io(&state, slot, "commit", || fs::rename(&tmp, &dst)) {
                    cleanup_part(&dst); rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                }
                if let Some(m) = expected_mtime(&info) {
                    if let Err(e) = set_mtime(&dst, m, &state, slot) {
                        rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                    }
                }
                if let Err(e) = manifest.append(&info.rel, &expected) {
                    rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                }
                if let Err(e) = journal.append(&state_key(&info)) {
                    rollback_write_progress(&state, slot, copied, &mut effective_written, start); control.alive.store(false, Ordering::Release); set_error(&state, slot, e); continue;
                }
                record_done(&state, slot);
            }
            Err(_) => {
                let cancelled = state.cancel.load(Ordering::Relaxed);
                let alive = control.alive.load(Ordering::Acquire);
                if let Some((info, f, _, copied, _)) = current.take() {
                    drop(f); cleanup_part(&dest.join(&info.rel)); rollback_write_progress(&state, slot, copied, &mut effective_written, start);
                }
                drain_queue(&rx);
                if let Err(e) = finish_logs(&mut manifest, &mut journal) { set_phase(&state, slot, DestPhase::Failed, Some(e)); return; }
                if cancelled { set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return; }
                if source_failed.load(Ordering::Acquire) { set_phase(&state, slot, DestPhase::Failed, Some("Error o cambio detectado en el origen.".into())); return; }
                if !alive {
                    let fatal = state.dests.lock().unwrap()[slot].error.as_deref().is_some_and(is_terminal_destination_error);
                    if fatal { set_phase(&state, slot, DestPhase::Failed, None); return; }
                    drop(manifest); drop(journal);
                    per_dest_worker(source, dest, files, state, slot, opts, CopyMode::Fallback);
                } else { set_phase(&state, slot, DestPhase::Done, None); }
                return;
            }
        }
        if !control.alive.load(Ordering::Acquire) {
            if let Some((info, f, _, copied, _)) = current.take() {
                drop(f); cleanup_part(&dest.join(&info.rel)); rollback_write_progress(&state, slot, copied, &mut effective_written, start);
            }
            drain_queue(&rx);
            if source_failed.load(Ordering::Acquire) || state.cancel.load(Ordering::Relaxed) { continue; }
            if let Err(e) = finish_logs(&mut manifest, &mut journal) { set_phase(&state, slot, DestPhase::Failed, Some(e)); return; }
            let fatal = state.dests.lock().unwrap()[slot].error.as_deref().is_some_and(is_terminal_destination_error);
            if fatal { set_phase(&state, slot, DestPhase::Failed, None); return; }
            drop(manifest); drop(journal);
            per_dest_worker(source.clone(), dest.clone(), Arc::clone(&files), Arc::clone(&state), slot, opts, CopyMode::Fallback);
            return;
        }
    }
}

enum SendOutcome { Ok, Timeout, Dead }

fn try_deliver_once(tx: &mpsc::Sender<FanoutItem>, item: FanoutItem, control: &DestControl) -> SendOutcome {
    match tx.send_timeout(item, SEND_POLL) {
        Ok(()) => { control.mark_progress(); SendOutcome::Ok }
        Err(crossbeam_channel::SendTimeoutError::Timeout(_)) => SendOutcome::Timeout,
        Err(crossbeam_channel::SendTimeoutError::Disconnected(_)) => SendOutcome::Dead,
    }
}

fn try_deliver_now(tx: &mpsc::Sender<FanoutItem>, item: FanoutItem, control: &DestControl) -> Result<(), FanoutItem> {
    match tx.try_send(item) {
        Ok(()) => { control.mark_progress(); Ok(()) }
        Err(crossbeam_channel::TrySendError::Full(item)) => Err(item),
        Err(crossbeam_channel::TrySendError::Disconnected(item)) => { control.alive.store(false, Ordering::Release); Err(item) }
    }
}

fn deliver_to_active(
    active: &mut Vec<usize>, senders: &[Option<mpsc::Sender<FanoutItem>>], controls: &[Arc<DestControl>],
    state: &JobState, counts_data: bool, make_item: impl Fn() -> FanoutItem,
) {
    let mut pending: Vec<(usize, FanoutItem)> = Vec::new();
    let initial_slots = active.clone();
    for slot in initial_slots {
        if !controls[slot].alive.load(Ordering::Acquire) { active.retain(|&s| s != slot); continue; }
        let Some(tx) = senders[slot].as_ref() else { active.retain(|&s| s != slot); continue; };
        if counts_data { controls[slot].queue_depth.fetch_add(1, Ordering::AcqRel); }
        match try_deliver_now(tx, make_item(), &controls[slot]) {
            Ok(()) => {
                if counts_data { state.dests.lock().unwrap()[slot].queue_depth = controls[slot].queue_depth.load(Ordering::Acquire); }
            }
            Err(item) if controls[slot].alive.load(Ordering::Acquire) => {
                if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                pending.push((slot, item));
            }
            Err(_) => {
                if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                active.retain(|&s| s != slot);
            }
        }
    }
    while !pending.is_empty() {
        if !wait_pause(state) { active.clear(); return; }
        let mut next = Vec::with_capacity(pending.len());
        for (slot, item) in pending {
            if !controls[slot].alive.load(Ordering::Acquire) { active.retain(|&s| s != slot); continue; }
            let Some(tx) = senders[slot].as_ref() else { active.retain(|&s| s != slot); continue; };
            if counts_data { controls[slot].queue_depth.fetch_add(1, Ordering::AcqRel); }
            match try_deliver_now(tx, item, &controls[slot]) {
                Ok(()) => {
                    if counts_data { state.dests.lock().unwrap()[slot].queue_depth = controls[slot].queue_depth.load(Ordering::Acquire); }
                }
                Err(item) if controls[slot].alive.load(Ordering::Acquire) => {
                    if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                    if controls[slot].stalled_for() >= STALL_THRESHOLD {
                        controls[slot].alive.store(false, Ordering::Release); active.retain(|&s| s != slot);
                    } else { next.push((slot, item)); }
                }
                Err(_) => {
                    if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                    active.retain(|&s| s != slot);
                }
            }
        }
        pending = next;
        if !pending.is_empty() { thread::sleep(DELIVERY_RETRY_SLEEP); }
    }
}

fn mark_skipped_all(state: &JobState, info: &FileInfo, mask: &[bool]) {
    let mut g = state.dests.lock().unwrap();
    for (slot, skip) in mask.iter().enumerate() {
        if *skip { g[slot].files_skip += 1; g[slot].files_done += 1; g[slot].written += info.size; }
    }
}

fn fanout_job(source: PathBuf, dests: Vec<PathBuf>, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, opts: CopyOpts) -> Vec<JoinHandle<()>> {
    let q = queue_depth_for(dests.len());
    let max_buffers = (RESERVED_RAM / BLOCK).max(8);
    let budget = BufferBudget::new(max_buffers, Arc::clone(&state.buffers_in_flight));
    let source_failed = Arc::new(AtomicBool::new(false));
    let mut senders = Vec::new(); let mut controls = Vec::new(); let mut handles = Vec::new();
    for (slot, dest) in dests.iter().cloned().enumerate() {
        let (tx, rx) = mpsc::bounded(q);
        let control = Arc::new(DestControl::new()); controls.push(Arc::clone(&control));
        let st = Arc::clone(&state); let src = source.clone(); let list = Arc::clone(&files);
        let failed = Arc::clone(&source_failed); let c = Arc::clone(&control);
        handles.push(thread::spawn(move || fanout_worker(src, dest, rx, c, list, st, slot, opts, failed)));
        senders.push(Some(tx));
    }
    let reader = thread::spawn(move || {
        let mut state_cache: Vec<HashSet<u128>> = dests.iter().map(|d| load_state(d)).collect();
        for info in files.iter() {
            if !wait_pause(&state) { break; }
            let key = state_key(info); let key_id = state_id_for_key(&key);
            let mut skip_mask = vec![false; dests.len()];
            for slot in 0..dests.len() {
                if !controls[slot].alive.load(Ordering::Acquire) { continue; }
                let src = source.join(&info.rel); let dst = dests[slot].join(&info.rel);
                skip_mask[slot] = state_cache[slot].contains(&key_id) || (opts.skip_same && same_enough(&src, &dst));
            }
            mark_skipped_all(&state, info, &skip_mask);
            let mut active: Vec<usize> = (0..dests.len()).filter(|&slot| !skip_mask[slot] && controls[slot].alive.load(Ordering::Acquire)).collect();
            deliver_to_active(&mut active, &senders, &controls, &state, false, || FanoutItem::Begin(info.clone()));
            if active.is_empty() { continue; }
            let path = source.join(&info.rel);
            if let Err(e) = validate_source_snapshot(&path, info) {
                source_failed.store(true, Ordering::Release); set_error(&state, 0, e); break;
            }
            let mut input = match File::open(&path) {
                Ok(f) => f,
                Err(e) => { source_failed.store(true, Ordering::Release); set_error(&state, 0, format!("[source] origen: {e}")); break; }
            };
            let mut hasher = blake3::Hasher::new(); let mut copied = 0u64; let mut read_ok = true; let mut abandoned = false;
            loop {
                if !wait_pause(&state) { read_ok = false; break; }
                if !budget.acquire(&state) { read_ok = false; break; }
                let mut raw = vec![0u8; BLOCK];
                let n = match input.read(&mut raw) {
                    Ok(0) => { budget.release(); break; }
                    Ok(n) => n,
                    Err(e) => { budget.release(); source_failed.store(true, Ordering::Release); set_error(&state, 0, format!("[source] lectura: {e}")); read_ok = false; break; }
                };
                raw.truncate(n); hasher.update(&raw); copied += n as u64;
                let buf = Arc::new(Buffer { data: raw.into_boxed_slice(), budget: Arc::clone(&budget) });
                deliver_to_active(&mut active, &senders, &controls, &state, true, || FanoutItem::Data(Arc::clone(&buf)));
                if active.is_empty() { abandoned = true; break; }
            }
            if !read_ok { break; }
            if abandoned { continue; }
            if copied != info.size {
                source_failed.store(true, Ordering::Release); set_error(&state, 0, format!("[source] origen cambió: {}", path.display())); break;
            }
            if let Err(e) = validate_source_snapshot(&path, info) {
                source_failed.store(true, Ordering::Release); set_error(&state, 0, e); break;
            }
            let hash = *hasher.finalize().as_bytes();
            for &slot in &active { state_cache[slot].insert(key_id); }
            deliver_to_active(&mut active, &senders, &controls, &state, false, || FanoutItem::End { hash });
        }
        senders.clear(); state.running.store(false, Ordering::Release);
    });
    handles.push(reader); handles
}

fn validate_job_paths(source: &Path, dests: &[PathBuf]) -> Result<(), String> {
    if !source.is_dir() { return Err("El origen debe ser una carpeta.".into()); }
    if dests.is_empty() { return Err("Agrega al menos un destino.".into()); }
    for d in dests {
        if dest_inside_source(source, d) { return Err(format!("El destino {} está dentro del origen.", d.display())); }
        fs::create_dir_all(d).map_err(|e| format!("destino {}: {e}", d.display()))?;
    }
    Ok(())
}

fn build_job(source: PathBuf, dests: Vec<PathBuf>, files: Arc<Vec<FileInfo>>, opts: CopyOpts) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    if files.is_empty() { return Err("El origen no tiene archivos.".into()); }
    let bytes_total: u64 = files.iter().map(|f| f.size).sum(); let files_total = files.len() as u64;
    let progress = dests.iter().map(|d| DestProgress {
        label: d.display().to_string(), written: 0, total: bytes_total, files_done: 0, files_skip: 0, files_err: 0, bps: 0.0,
        phase: DestPhase::Idle, error: None, last_file: String::new(), mode: CopyMode::Fanout, queue_depth: 0, retries: 0,
    }).collect();
    let max_buffers = (RESERVED_RAM / BLOCK).max(8);
    let state = Arc::new(JobState {
        running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false), files_total: AtomicU64::new(files_total),
        bytes_total: AtomicU64::new(bytes_total), buffers_in_flight: Arc::new(AtomicUsize::new(0)), max_buffers, fanout: true, dests: Mutex::new(progress),
    });
    let handles = fanout_job(source, dests, files, Arc::clone(&state), opts); Ok((state, handles))
}

pub(crate) fn start_job_with_files(source: PathBuf, dests: Vec<PathBuf>, files: Arc<Vec<FileInfo>>, opts: CopyOpts) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    validate_job_paths(&source, &dests)?; build_job(source, dests, files, opts)
}

pub fn start_job(source: PathBuf, dests: Vec<PathBuf>, opts: CopyOpts) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    validate_job_paths(&source, &dests)?; let files = Arc::new(list_files(&source)?); build_job(source, dests, files, opts)
}

pub fn format_bps(bps: f64) -> String {
    if bps >= 1_073_741_824.0 { format!("{:.2} GB/s", bps / 1_073_741_824.0) }
    else if bps >= 1_048_576.0 { format!("{:.1} MB/s", bps / 1_048_576.0) }
    else if bps >= 1024.0 { format!("{:.0} KB/s", bps / 1024.0) }
    else { format!("{:.0} B/s", bps) }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let p = std::env::temp_dir().join(format!("disk-duplicator-{name}-{stamp}")); fs::create_dir_all(&p).unwrap(); p
    }
    fn info_for(path: &Path, rel: &str) -> FileInfo {
        let meta = fs::metadata(path).unwrap(); FileInfo { rel: PathBuf::from(rel), size: meta.len(), mtime_ns: metadata_mtime_ns(&meta) }
    }
    #[test] fn state_key_is_stable() {
        let a = FileInfo { rel: PathBuf::from("a/b.txt"), size: 42, mtime_ns: 7 };
        assert_eq!(state_key(&a), state_key(&a.clone())); assert_ne!(state_key(&a), state_key(&FileInfo { size: 43, ..a }));
        assert_eq!(state_id(&a), state_id(&a.clone()));
    }
    #[test] fn source_snapshot_detects_mutation() {
        let root = temp_dir("source-snapshot"); let src = root.join("a.bin");
        fs::write(&src, b"abc").unwrap(); let info = info_for(&src, "a.bin");
        assert!(validate_source_snapshot(&src, &info).is_ok());
        fs::write(&src, b"changed-data").unwrap(); assert!(validate_source_snapshot(&src, &info).is_err());
        let _ = fs::remove_dir_all(root);
    }
    #[test] fn journal_finish_persists_all_keys() {
        let root = temp_dir("journal"); let mut journal = StateJournal::open(&root).unwrap();
        journal.append("a|1|1").unwrap(); journal.append("b|2|2").unwrap(); journal.finish().unwrap();
        let loaded = load_state(&root); assert!(loaded.contains(&state_id_for_key("a|1|1"))); assert!(loaded.contains(&state_id_for_key("b|2|2"))); let _ = fs::remove_dir_all(root);
    }
    #[test] fn manifest_finish_persists_multiple_entries() {
        let root = temp_dir("manifest"); let mut manifest = ManifestWriter::open(&root).unwrap();
        let a = blake3::hash(b"a"); let b = blake3::hash(b"b");
        manifest.append(Path::new("a.bin"), &a).unwrap(); manifest.append(Path::new("folder/b.bin"), &b).unwrap(); manifest.finish().unwrap();
        let text = fs::read_to_string(manifest_path(&root)).unwrap();
        assert!(text.contains(&format!("{}  a.bin", a.to_hex()))); assert!(text.contains(&format!("{}  folder/b.bin", b.to_hex()))); let _ = fs::remove_dir_all(root);
    }
    #[test] fn queue_scales_with_dests() {
        assert_eq!(queue_depth_for(1), MAX_QUEUE); assert!(queue_depth_for(200) >= MIN_QUEUE); assert!(queue_depth_for(200) <= queue_depth_for(2));
    }
    #[test] fn io_policy_does_not_retry_permission_or_space() {
        assert_eq!(classify_io_error(&io::Error::new(io::ErrorKind::PermissionDenied, "x")), IoClass::Permission);
        let disk_full = io::Error::from_raw_os_error(112); assert_eq!(classify_io_error(&disk_full), IoClass::NoSpace);
    }
    #[test] fn runtime_layout_rejects_symlink_like_parent_conflicts() {
        let root = temp_dir("runtime-layout"); fs::write(root.join("parent"), b"x").unwrap();
        assert!(validate_destination_layout_runtime(&root, Path::new("parent/file.bin")).is_err());
        let _ = fs::remove_dir_all(root);
    }
    #[test] fn atomic_copy_leaves_old_on_fail() {
        let root = temp_dir("atomic"); let src = root.join("src.bin"); let dst_root = root.join("out"); let dst = dst_root.join("dst.bin");
        fs::create_dir_all(&dst_root).unwrap(); fs::write(&src, b"new-data").unwrap(); fs::write(&dst, b"old-data").unwrap(); let info = info_for(&src, "dst.bin");
        let gauge = Arc::new(AtomicUsize::new(0));
        let state = JobState {
            running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false), files_total: AtomicU64::new(0), bytes_total: AtomicU64::new(0),
            buffers_in_flight: gauge, max_buffers: 2, fanout: false, dests: Mutex::new(vec![DestProgress {
                label: String::new(), written: 0, total: 0, files_done: 0, files_skip: 0, files_err: 0, bps: 0.0, phase: DestPhase::Idle,
                error: None, last_file: String::new(), mode: CopyMode::PerDestination, queue_depth: 0, retries: 0,
            }]),
        };
        let mut effective_written = 0u64;
        copy_file_atomic(&src, &dst, &dst_root, &info, true, &state, 0, &mut effective_written, Instant::now()).unwrap();
        assert_eq!(fs::read(&dst).unwrap(), b"new-data"); assert_eq!(state.snapshot()[0].written, 8); assert!(!part_path(&dst).exists()); let _ = fs::remove_dir_all(root);
    }
    #[test] fn progress_rolls_back_failed_attempt() {
        let gauge = Arc::new(AtomicUsize::new(0));
        let state = JobState {
            running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false), files_total: AtomicU64::new(0), bytes_total: AtomicU64::new(0),
            buffers_in_flight: gauge, max_buffers: 2, fanout: false, dests: Mutex::new(vec![DestProgress {
                label: String::new(), written: 0, total: 10, files_done: 0, files_skip: 0, files_err: 0, bps: 0.0, phase: DestPhase::Copying,
                error: None, last_file: String::new(), mode: CopyMode::PerDestination, queue_depth: 0, retries: 0,
            }]),
        };
        let mut effective_written = 0u64; let start = Instant::now(); record_write_progress(&state, 0, 7, &mut effective_written, start);
        assert_eq!(state.snapshot()[0].written, 7); rollback_write_progress(&state, 0, 7, &mut effective_written, start);
        assert_eq!(state.snapshot()[0].written, 0); assert_eq!(effective_written, 0);
    }
    #[test] fn budget_never_exceeds_limit() {
        let gauge = Arc::new(AtomicUsize::new(0)); let budget = BufferBudget::new(2, Arc::clone(&gauge));
        let state = JobState { running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false), files_total: AtomicU64::new(0), bytes_total: AtomicU64::new(0), buffers_in_flight: gauge, max_buffers: 2, fanout: true, dests: Mutex::new(Vec::new()) };
        budget.acquire(&state); let a = Arc::new(Buffer { data: vec![1].into_boxed_slice(), budget: Arc::clone(&budget) });
        budget.acquire(&state); let b = Arc::new(Buffer { data: vec![2].into_boxed_slice(), budget: Arc::clone(&budget) });
        assert_eq!(state.buffers_in_flight.load(Ordering::Relaxed), 2); drop(a); drop(b); assert_eq!(state.buffers_in_flight.load(Ordering::Relaxed), 0);
    }
    #[test] fn format_is_sane() { assert_eq!(format_bps(0.0), "0 B/s"); assert!(format_bps(1024.0).contains("KB/s")); }
}

#[cfg(test)]
mod regression_tests {
    use super::*;
    use std::time::Duration as Dur;

    #[test]
    fn momentary_full_queue_does_not_kill_destination() {
        let (tx, rx) = mpsc::bounded::<FanoutItem>(1); let control = DestControl::new(); let budget = BufferBudget::new(8, Arc::new(AtomicUsize::new(0)));
        let buf1 = Arc::new(Buffer { data: vec![0u8; 4].into_boxed_slice(), budget: Arc::clone(&budget) });
        let buf2 = Arc::new(Buffer { data: vec![0u8; 4].into_boxed_slice(), budget: Arc::clone(&budget) });
        assert!(matches!(try_deliver_once(&tx, FanoutItem::Data(buf1), &control), SendOutcome::Ok)); assert!(control.alive.load(Ordering::Relaxed));
        let outcome1 = try_deliver_once(&tx, FanoutItem::Data(buf2), &control); assert!(matches!(outcome1, SendOutcome::Timeout)); assert!(control.alive.load(Ordering::Relaxed));
        let _ = rx.recv(); let buf3 = Arc::new(Buffer { data: vec![0u8; 4].into_boxed_slice(), budget: Arc::clone(&budget) });
        let outcome2 = try_deliver_once(&tx, FanoutItem::Data(buf3), &control); assert!(matches!(outcome2, SendOutcome::Ok)); assert!(control.alive.load(Ordering::Relaxed));
    }

    #[test]
    fn sustained_stall_past_threshold_eventually_degrades() {
        let (tx, _rx) = mpsc::bounded::<FanoutItem>(1); let control = DestControl::new(); let budget = BufferBudget::new(8, Arc::new(AtomicUsize::new(0)));
        let buf1 = Arc::new(Buffer { data: vec![0u8; 4].into_boxed_slice(), budget: Arc::clone(&budget) });
        assert!(matches!(try_deliver_once(&tx, FanoutItem::Data(buf1), &control), SendOutcome::Ok));
        *control.last_progress.lock().unwrap() = Instant::now() - STALL_THRESHOLD - Duration::from_secs(1);
        let buf2 = Arc::new(Buffer { data: vec![0u8; 4].into_boxed_slice(), budget: Arc::clone(&budget) });
        let outcome = try_deliver_once(&tx, FanoutItem::Data(buf2), &control); assert!(matches!(outcome, SendOutcome::Timeout)); assert!(control.stalled_for() >= STALL_THRESHOLD);
    }

    #[test]
    fn nested_new_destination_is_rejected() {
        let root = std::env::temp_dir().join(format!("dd-regr-nest-{}", std::process::id())); let _ = fs::remove_dir_all(&root);
        let source = root.join("Fotos"); fs::create_dir_all(&source).unwrap(); fs::write(source.join("a.jpg"), vec![1u8; 100]).unwrap();
        let dest_new_nested = source.join("Backup"); assert!(!dest_new_nested.exists());
        let opts = CopyOpts { verify: false, skip_same: false, keep_going: true }; let result = start_job(source.clone(), vec![dest_new_nested], opts);
        assert!(result.is_err()); let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn slow_destination_does_not_starve_fast_one() {
        let root = std::env::temp_dir().join(format!("dd-regr-mix-{}", std::process::id())); let _ = fs::remove_dir_all(&root); fs::create_dir_all(root.join("src")).unwrap();
        for i in 0..5 { fs::write(root.join("src").join(format!("f{i}.bin")), vec![i as u8; 2_000_000]).unwrap(); }
        let fast = root.join("fast"); let slow = root.join("slow"); let opts = CopyOpts { verify: true, skip_same: false, keep_going: true };
        let (state, handles) = start_job(root.join("src"), vec![fast.clone(), slow.clone()], opts).unwrap(); let t0 = Instant::now();
        while state.running.load(Ordering::Relaxed) && t0.elapsed() < Dur::from_secs(30) { thread::sleep(Dur::from_millis(20)); }
        for h in handles { h.join().unwrap(); }
        let snap = state.snapshot(); for dp in &snap { assert_eq!(dp.phase, DestPhase::Done, "{}: {:?}", dp.label, dp.error); }
        for i in 0..5 { let name = format!("f{i}.bin"); assert_eq!(fs::read(fast.join(&name)).unwrap().len(), 2_000_000); assert_eq!(fs::read(slow.join(&name)).unwrap().len(), 2_000_000); }
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn fast_destination_is_dispatched_before_slow_queue_wait() {
        let (slow_tx, slow_rx) = mpsc::bounded::<FanoutItem>(1); let (fast_tx, fast_rx) = mpsc::bounded::<FanoutItem>(1);
        let slow_control = Arc::new(DestControl::new()); let fast_control = Arc::new(DestControl::new());
        let controls = vec![Arc::clone(&slow_control), Arc::clone(&fast_control)]; let senders = vec![Some(slow_tx), Some(fast_tx)];
        let _ = senders[0].as_ref().unwrap().send(FanoutItem::End { hash: [0; 32] }); let gauge = Arc::new(AtomicUsize::new(0));
        let state = Arc::new(JobState {
            running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false), files_total: AtomicU64::new(1), bytes_total: AtomicU64::new(1), buffers_in_flight: gauge, max_buffers: 8, fanout: true,
            dests: Mutex::new(vec![
                DestProgress { label: "slow".into(), written: 0, total: 1, files_done: 0, files_skip: 0, files_err: 0, bps: 0.0, phase: DestPhase::Idle, error: None, last_file: String::new(), mode: CopyMode::Fanout, queue_depth: 0, retries: 0 },
                DestProgress { label: "fast".into(), written: 0, total: 1, files_done: 0, files_skip: 0, files_err: 0, bps: 0.0, phase: DestPhase::Idle, error: None, last_file: String::new(), mode: CopyMode::Fanout, queue_depth: 0, retries: 0 },
            ]),
        });
        let state_for_thread = Arc::clone(&state); let started = Instant::now();
        let handle = thread::spawn(move || { let mut active = vec![0usize, 1usize]; deliver_to_active(&mut active, &senders, &controls, &state_for_thread, false, || FanoutItem::End { hash: [1; 32] }); });
        let fast_item = fast_rx.recv_timeout(Duration::from_millis(50)).expect("el destino rápido debe recibir sin esperar 150 ms");
        assert!(matches!(fast_item, FanoutItem::End { hash } if hash == [1; 32])); assert!(started.elapsed() < Duration::from_millis(100));
        let _ = slow_rx.recv(); handle.join().unwrap();
    }
}
