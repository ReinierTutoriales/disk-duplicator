use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{mpsc, Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use walkdir::WalkDir;

const BLOCK: usize = 4 * 1024 * 1024;
const FANOUT_MAX_DESTS: usize = 12;
const FANOUT_MAX_MEMORY: usize = 1024 * 1024 * 1024;
const FANOUT_QUEUE: usize = 32;
const FANOUT_DEGRADE_AFTER: Duration = Duration::from_secs(5);
const RETRIES: usize = 2;

#[derive(Clone, Copy)]
pub struct CopyOpts { pub verify: bool, pub skip_same: bool, pub keep_going: bool }

#[derive(Clone, Copy, PartialEq, Eq)]
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
            let (next, _) = self.cv.wait_timeout(n, Duration::from_millis(100)).unwrap(); n = next;
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

#[derive(Clone)]
struct FileInfo { rel: PathBuf, size: u64, mtime_ns: u128 }
enum FanoutItem { Begin(FileInfo), Data(BufferRef), End }
struct DestControl { degraded: AtomicBool, queue_depth: AtomicUsize }

fn list_files(root: &Path) -> Result<Vec<FileInfo>, String> {
    let mut out = Vec::new();
    for entry in WalkDir::new(root).follow_links(false) {
        let entry = entry.map_err(|e| e.to_string())?;
        if !entry.file_type().is_file() { continue; }
        let meta = entry.metadata().map_err(|e| e.to_string())?;
        let rel = entry.path().strip_prefix(root).map_err(|e| e.to_string())?.to_path_buf();
        let mtime_ns = meta.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| d.as_nanos()).unwrap_or(0);
        out.push(FileInfo { rel, size: meta.len(), mtime_ns });
    }
    out.sort_by(|a, b| a.rel.cmp(&b.rel)); out
        .into_iter().collect::<Vec<_>>().pipe_ok()
}

trait PipeOk<T> { fn pipe_ok(self) -> Result<T, String>; }
impl PipeOk<Vec<FileInfo>> for Vec<FileInfo> { fn pipe_ok(self) -> Result<Vec<FileInfo>, String> { Ok(self) } }

fn dest_inside_source(src: &Path, dst: &Path) -> bool {
    let Ok(s) = src.canonicalize() else { return false; }; let Ok(d) = dst.canonicalize() else { return false; }; d == s || d.starts_with(&s)
}
fn wait_pause(state: &JobState) { while state.pause.load(Ordering::Relaxed) && !state.cancel.load(Ordering::Relaxed) { thread::sleep(Duration::from_millis(40)); } }
fn set_phase(state: &JobState, slot: usize, phase: DestPhase, err: Option<String>) { let mut g = state.dests.lock().unwrap(); g[slot].phase = phase; if err.is_some() { g[slot].error = err; } }
fn set_error(state: &JobState, slot: usize, msg: String) { state.dests.lock().unwrap()[slot].error = Some(msg); }
fn same_enough(src: &Path, dst: &Path) -> bool {
    let Ok(a) = fs::metadata(src) else { return false; }; let Ok(b) = fs::metadata(dst) else { return false; };
    if a.len() != b.len() { return false; }
    match (a.modified(), b.modified()) { (Ok(x), Ok(y)) => x == y, _ => false }
}
fn state_dir(dest: &Path) -> PathBuf { dest.join(".disk-duplicator") }
fn state_path(dest: &Path) -> PathBuf { state_dir(dest).join("completed.jsonl") }
fn state_key(info: &FileInfo) -> String {
    let mut hex = String::with_capacity(info.rel.as_os_str().len() * 2);
    for b in info.rel.to_string_lossy().as_bytes() { hex.push_str(&format!("{b:02x}")); }
    format!("{hex}|{}|{}", info.size, info.mtime_ns)
}
fn load_state(dest: &Path) -> HashSet<String> {
    let Ok(text) = fs::read_to_string(state_path(dest)) else { return HashSet::new(); };
    text.lines().filter_map(|line| { let (_, rest) = line.split_once("\"key\":\"")?; let (key, _) = rest.split_once("\"}")?; Some(key.to_owned()) }).collect()
}
fn append_state(dest: &Path, key: &str) -> Result<(), String> {
    fs::create_dir_all(state_dir(dest)).map_err(|e| format!("state mkdir: {e}"))?;
    let mut f = OpenOptions::new().create(true).append(true).open(state_path(dest)).map_err(|e| format!("state open: {e}"))?;
    writeln!(f, r#"{{"key":"{}"}}"#, key).map_err(|e| format!("state write: {e}"))?;
    f.sync_all().map_err(|e| format!("state sync: {e}"))?; Ok(())
}
fn temp_path(dst: &Path) -> PathBuf { let name = dst.file_name().and_then(|n| n.to_str()).unwrap_or("file"); dst.with_file_name(format!(".{name}.disk-duplicator.part")) }
fn cleanup_temp(dst: &Path) { let _ = fs::remove_file(temp_path(dst)); }
fn commit_temp(temp: &Path, dst: &Path) -> Result<(), String> {
    if let Some(parent) = dst.parent() { fs::create_dir_all(parent).map_err(|e| format!("mkdir: {e}"))?; }
    fs::rename(temp, dst).map_err(|e| format!("commit {}: {e}", dst.display()))
}
fn hash_file(path: &Path) -> Result<blake3::Hash, String> {
    let mut f = File::open(path).map_err(|e| format!("verificar: {e}"))?; let mut buf = vec![0u8; BLOCK]; let mut h = blake3::Hasher::new();
    loop { let n = f.read(&mut buf).map_err(|e| format!("verificar: {e}"))?; if n == 0 { break; } h.update(&buf[..n]); } Ok(h.finalize())
}
fn set_mtime(dst: &Path, mtime: std::time::SystemTime) -> Result<(), String> {
    let mut f = OpenOptions::new().write(true).open(dst).map_err(|e| format!("metadata: {e}"))?;
    f.set_modified(mtime).map_err(|e| format!("metadata: {e}"))?; f.sync_all().map_err(|e| format!("metadata sync: {e}"))?; Ok(())
}
fn copy_file_atomic(src: &Path, dst: &Path, verify: bool) -> Result<(), String> {
    if let Some(parent) = dst.parent() { fs::create_dir_all(parent).map_err(|e| format!("mkdir: {e}"))?; }
    let meta = fs::metadata(src).map_err(|e| format!("metadata: {e}"))?; let mut input = File::open(src).map_err(|e| format!("abrir origen: {e}"))?;
    let tmp = temp_path(dst); cleanup_temp(dst);
    let result = (|| {
        let mut output = File::create(&tmp).map_err(|e| format!("crear temporal: {e}"))?; let mut buf = vec![0u8; BLOCK]; let mut h = blake3::Hasher::new(); let mut copied = 0u64;
        loop { let n = input.read(&mut buf).map_err(|e| format!("lectura: {e}"))?; if n == 0 { break; } copied += n as u64; output.write_all(&buf[..n]).map_err(|e| format!("escritura: {e}"))?; h.update(&buf[..n]); }
        if copied != meta.len() { return Err(format!("origen cambió durante la lectura: {}", src.display())); }
        output.flush().map_err(|e| format!("flush: {e}"))?; output.sync_all().map_err(|e| format!("sync: {e}"))?; drop(output);
        if verify { let expected = h.finalize(); if hash_file(&tmp)? != expected { return Err(format!("BLAKE3 no coincide: {}", src.display())); } }
        commit_temp(&tmp, dst)?; if let Ok(m) = meta.modified() { set_mtime(dst, m)?; } Ok(())
    })();
    if result.is_err() { cleanup_temp(dst); } result
}
fn record_skip(state: &JobState, slot: usize, size: u64) { let mut g = state.dests.lock().unwrap(); g[slot].files_skip += 1; g[slot].files_done += 1; g[slot].written += size; }
fn record_done(state: &JobState, slot: usize, size: u64, start: Instant) { let mut g = state.dests.lock().unwrap(); g[slot].files_done += 1; g[slot].written += size; let secs = start.elapsed().as_secs_f64(); if secs > 0.0 { g[slot].bps = g[slot].written as f64 / secs; } }
fn copy_one_with_retries(src: &Path, dst: &Path, opts: CopyOpts, state: &JobState, slot: usize) -> bool {
    for attempt in 0..=RETRIES {
        if state.cancel.load(Ordering::Relaxed) { return false; }
        match copy_file_atomic(src, dst, opts.verify) {
            Ok(()) => return true,
            Err(e) if attempt < RETRIES => { state.dests.lock().unwrap()[slot].retries += 1; thread::sleep(Duration::from_millis(75 * (attempt as u64 + 1))); wait_pause(state); }
            Err(e) => { set_error(state, slot, e); return false; }
        }
    }
    false
}
fn per_dest_worker(source: PathBuf, dest: PathBuf, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, slot: usize, opts: CopyOpts, mode: CopyMode) {
    let start = Instant::now(); { let mut g = state.dests.lock().unwrap(); g[slot].mode = mode; g[slot].phase = DestPhase::Copying; }
    let completed = load_state(&dest);
    for info in files.iter() {
        if state.cancel.load(Ordering::Relaxed) { set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return; }
        wait_pause(&state); if state.cancel.load(Ordering::Relaxed) { set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return; }
        let key = state_key(info); let src = source.join(&info.rel); let dst = dest.join(&info.rel); state.dests.lock().unwrap()[slot].last_file = info.rel.to_string_lossy().into_owned();
        if completed.contains(&key) || (opts.skip_same && same_enough(&src, &dst)) { record_skip(&state, slot, info.size); continue; }
        if !copy_one_with_retries(&src, &dst, opts, &state, slot) {
            if state.cancel.load(Ordering::Relaxed) { set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return; }
            state.dests.lock().unwrap()[slot].files_err += 1; if !opts.keep_going { set_phase(&state, slot, DestPhase::Failed, None); return; } continue;
        }
        if let Err(e) = append_state(&dest, &key) { set_error(&state, slot, e); state.dests.lock().unwrap()[slot].files_err += 1; if !opts.keep_going { set_phase(&state, slot, DestPhase::Failed, None); return; } continue; }
        record_done(&state, slot, info.size, start);
    }
    let errs = state.dests.lock().unwrap()[slot].files_err; if errs == 0 { set_phase(&state, slot, DestPhase::Done, None); } else { set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es)."))); }
}
fn drain_queue(rx: &mpsc::Receiver<FanoutItem>) { while rx.try_recv().is_ok() {} }
fn fanout_worker(source: PathBuf, dest: PathBuf, rx: mpsc::Receiver<FanoutItem>, control: Arc<DestControl>, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, slot: usize, opts: CopyOpts, source_failed: Arc<AtomicBool>) {
    let start = Instant::now(); let mut current: Option<(FileInfo, File, blake3::Hasher, u64)> = None; set_phase(&state, slot, DestPhase::Copying, None);
    loop {
        match rx.recv() {
            Ok(FanoutItem::Begin(info)) => {
                if control.degraded.load(Ordering::Acquire) { continue; }
                let dst = dest.join(&info.rel); if let Some(parent) = dst.parent() { if let Err(e) = fs::create_dir_all(parent) { control.degraded.store(true, Ordering::Release); set_error(&state, slot, format!("mkdir: {e}")); continue; } }
                cleanup_temp(&dst); match File::create(temp_path(&dst)) { Ok(f) => current = Some((info, f, blake3::Hasher::new(), 0)), Err(e) => { control.degraded.store(true, Ordering::Release); set_error(&state, slot, format!("temporal: {e}")); } }
            }
            Ok(FanoutItem::Data(buf)) => {
                if let Some((info, f, h, copied)) = current.as_mut() { if let Err(e) = f.write_all(&buf.data) { control.degraded.store(true, Ordering::Release); set_error(&state, slot, format!("escritura {}: {e}", info.rel.display())); } else { h.update(&buf.data); *copied += buf.data.len() as u64; } }
                control.queue_depth.fetch_sub(1, Ordering::AcqRel); state.dests.lock().unwrap()[slot].queue_depth = control.queue_depth.load(Ordering::Acquire);
            }
            Ok(FanoutItem::End) => {
                let Some((info, mut f, hasher, copied)) = current.take() else { continue; }; let dst = dest.join(&info.rel); let tmp = temp_path(&dst);
                if control.degraded.load(Ordering::Acquire) { drop(f); cleanup_temp(&dst); continue; }
                if copied != info.size { drop(f); cleanup_temp(&dst); control.degraded.store(true, Ordering::Release); set_error(&state, slot, format!("tamaño inesperado en {}", info.rel.display())); continue; }
                if let Err(e) = f.flush().and_then(|_| f.sync_all()) { drop(f); cleanup_temp(&dst); control.degraded.store(true, Ordering::Release); set_error(&state, slot, format!("sync: {e}")); continue; }
                drop(f);
                if opts.verify { set_phase(&state, slot, DestPhase::Verifying, None); let expected = hasher.finalize(); match hash_file(&tmp) { Ok(actual) if actual == expected => {}, Ok(_) => { cleanup_temp(&dst); control.degraded.store(true, Ordering::Release); set_error(&state, slot, format!("BLAKE3 no coincide: {}", dst.display())); continue; }, Err(e) => { cleanup_temp(&dst); control.degraded.store(true, Ordering::Release); set_error(&state, slot, e); continue; } } }
                if let Err(e) = commit_temp(&tmp, &dst) { cleanup_temp(&dst); control.degraded.store(true, Ordering::Release); set_error(&state, slot, e); continue; }
                if let Ok(m) = fs::metadata(source.join(&info.rel)).and_then(|m| m.modified()) { if let Err(e) = set_mtime(&dst, m) { control.degraded.store(true, Ordering::Release); set_error(&state, slot, e); continue; } }
                if let Err(e) = append_state(&dest, &state_key(&info)) { control.degraded.store(true, Ordering::Release); set_error(&state, slot, e); continue; }
                state.dests.lock().unwrap()[slot].last_file = info.rel.to_string_lossy().into_owned(); record_done(&state, slot, info.size, start); set_phase(&state, slot, DestPhase::Copying, None);
            }
            Err(_) => {
                let cancelled = state.cancel.load(Ordering::Relaxed); let degraded = control.degraded.load(Ordering::Acquire);
                if let Some((info, f, _, _)) = current.take() { drop(f); cleanup_temp(&dest.join(&info.rel)); }
                drain_queue(&rx);
                if cancelled { set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return; }
                if source_failed.load(Ordering::Acquire) { set_phase(&state, slot, DestPhase::Failed, Some("Error de lectura del origen.".into())); return; }
                if degraded { per_dest_worker(source, dest, files, state, slot, opts, CopyMode::Fallback); } else { set_phase(&state, slot, DestPhase::Done, None); }
                return;
            }
        }
        if control.degraded.load(Ordering::Acquire) {
            if let Some((info, f, _, _)) = current.take() { drop(f); cleanup_temp(&dest.join(&info.rel)); }
            drain_queue(&rx);
            if source_failed.load(Ordering::Acquire) || state.cancel.load(Ordering::Relaxed) { continue; }
            per_dest_worker(source.clone(), dest.clone(), Arc::clone(&files), Arc::clone(&state), slot, opts, CopyMode::Fallback); return;
        }
    }
}
fn reserve_and_send(tx: &mpsc::SyncSender<FanoutItem>, item: FanoutItem, control: &DestControl, state: &JobState, counts_data: bool) -> Result<(), FanoutItem> {
    let started = Instant::now(); let mut item = item; let mut reserved = false;
    if counts_data { control.queue_depth.fetch_add(1, Ordering::AcqRel); reserved = true; }
    loop {
        if state.cancel.load(Ordering::Relaxed) || control.degraded.load(Ordering::Acquire) { if reserved { control.queue_depth.fetch_sub(1, Ordering::AcqRel); } return Err(item); }
        match tx.try_send(item) {
            Ok(()) => return Ok(()),
            Err(mpsc::TrySendError::Full(v)) => { item = v; if started.elapsed() >= FANOUT_DEGRADE_AFTER { control.degraded.store(true, Ordering::Release); if reserved { control.queue_depth.fetch_sub(1, Ordering::AcqRel); } return Err(item); } thread::sleep(Duration::from_millis(2)); }
            Err(mpsc::TrySendError::Disconnected(v)) => { control.degraded.store(true, Ordering::Release); if reserved { control.queue_depth.fetch_sub(1, Ordering::AcqRel); } return Err(v); }
        }
    }
}
fn mark_skipped_all(state: &JobState, info: &FileInfo, mask: &[bool]) { let mut g = state.dests.lock().unwrap(); for (slot, skip) in mask.iter().enumerate() { if *skip { g[slot].files_skip += 1; g[slot].files_done += 1; g[slot].written += info.size; } } }
fn fanout_job(source: PathBuf, dests: Vec<PathBuf>, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, opts: CopyOpts) -> Vec<JoinHandle<()>> {
    let max_buffers = (FANOUT_MAX_MEMORY / BLOCK).max(8); let budget = BufferBudget::new(max_buffers, Arc::clone(&state.buffers_in_flight)); let source_failed = Arc::new(AtomicBool::new(false));
    let mut senders = Vec::new(); let mut controls = Vec::new(); let mut handles = Vec::new();
    for (slot, dest) in dests.iter().cloned().enumerate() {
        let (tx, rx) = mpsc::sync_channel(FANOUT_QUEUE); let control = Arc::new(DestControl { degraded: AtomicBool::new(false), queue_depth: AtomicUsize::new(0) }); controls.push(Arc::clone(&control));
        let st = Arc::clone(&state); let src = source.clone(); let list = Arc::clone(&files); let failed = Arc::clone(&source_failed); let c = Arc::clone(&control);
        handles.push(thread::spawn(move || fanout_worker(src, dest, rx, c, list, st, slot, opts, failed))); senders.push(Some(tx));
    }
    let reader = thread::spawn(move || {
        let state_cache: Vec<HashSet<String>> = dests.iter().map(|d| load_state(d)).collect();
        for info in files.iter() {
            if state.cancel.load(Ordering::Relaxed) { break; } wait_pause(&state); if state.cancel.load(Ordering::Relaxed) { break; }
            let key = state_key(info); let mut skip_mask = vec![false; dests.len()];
            for slot in 0..dests.len() { if controls[slot].degraded.load(Ordering::Acquire) { continue; } let src = source.join(&info.rel); let dst = dests[slot].join(&info.rel); skip_mask[slot] = state_cache[slot].contains(&key) || (opts.skip_same && same_enough(&src, &dst)); }
            mark_skipped_all(&state, info, &skip_mask); let mut active = Vec::new();
            for slot in 0..dests.len() { if skip_mask[slot] || controls[slot].degraded.load(Ordering::Acquire) || senders[slot].is_none() { continue; } let tx = senders[slot].as_ref().unwrap(); match reserve_and_send(tx, FanoutItem::Begin(info.clone()), &controls[slot], &state, false) { Ok(()) => active.push(slot), Err(_) => senders[slot] = None } }
            if active.is_empty() { continue; }
            let src_path = source.join(&info.rel); let mut input = match File::open(&src_path) { Ok(f) => f, Err(e) => { source_failed.store(true, Ordering::Release); for &slot in &active { set_error(&state, slot, format!("{}: {e}", info.rel.display())); } break; } };
            let mut read_total = 0u64; let mut read_error = None;
            loop {
                if state.cancel.load(Ordering::Relaxed) { break; } wait_pause(&state); if !budget.acquire(&state) { break; }
                let mut data = vec![0u8; BLOCK]; let n = match input.read(&mut data) { Ok(n) => n, Err(e) => { read_error = Some(e.to_string()); 0 } };
                if n == 0 { let holder = Buffer { data: Vec::new().into_boxed_slice(), budget: Arc::clone(&budget) }; drop(holder); break; }
                read_total += n as u64; data.truncate(n); let buffer = Arc::new(Buffer { data: data.into_boxed_slice(), budget: Arc::clone(&budget) }); let mut delivered = 0usize;
                for &slot in &active { if controls[slot].degraded.load(Ordering::Acquire) { continue; } let tx = match senders[slot].as_ref() { Some(t) => t, None => continue }; match reserve_and_send(tx, FanoutItem::Data(Arc::clone(&buffer)), &controls[slot], &state, true) { Ok(()) => delivered += 1, Err(_) => senders[slot] = None } }
                drop(buffer); if delivered == 0 { break; }
            }
            if read_error.is_some() || read_total != info.size { source_failed.store(true, Ordering::Release); let msg = read_error.map(|e| format!("lectura {}: {e}", info.rel.display())).unwrap_or_else(|| format!("origen cambió durante la lectura: {}", info.rel.display())); for &slot in &active { set_error(&state, slot, msg.clone()); } break; }
            if state.cancel.load(Ordering::Relaxed) { break; }
            for &slot in &active { if controls[slot].degraded.load(Ordering::Acquire) { continue; } let tx = match senders[slot].as_ref() { Some(t) => t, None => continue }; if reserve_and_send(tx, FanoutItem::End, &controls[slot], &state, false).is_err() { senders[slot] = None; } }
        }
        drop(senders);
    });
    handles.push(reader); handles
}

pub fn start_job(source: PathBuf, dests: Vec<PathBuf>, opts: CopyOpts) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    if !source.is_dir() { return Err("El origen debe ser una carpeta.".into()); } if dests.is_empty() { return Err("Agrega al menos un destino.".into()); }
    let mut unique = HashSet::new(); for d in &dests { let key = d.canonicalize().unwrap_or_else(|_| d.to_path_buf()).to_string_lossy().to_lowercase(); if !unique.insert(key) { return Err(format!("Destino duplicado: {}", d.display())); } if dest_inside_source(&source, d) { return Err(format!("El destino {} está dentro del origen.", d.display())); } fs::create_dir_all(d).map_err(|e| format!("destino {}: {e}", d.display()))?; }
    let files = Arc::new(list_files(&source)?); if files.is_empty() { return Err("El origen no tiene archivos.".into()); }
    let bytes_total: u64 = files.iter().map(|f| f.size).sum(); let use_fanout = dests.len() > 1 && dests.len() <= FANOUT_MAX_DESTS;
    let progress = dests.iter().map(|d| DestProgress { label: d.display().to_string(), written: 0, total: bytes_total, files_done: 0, files_skip: 0, files_err: 0, bps: 0.0, phase: DestPhase::Idle, error: None, last_file: String::new(), mode: if use_fanout { CopyMode::Fanout } else { CopyMode::PerDestination }, queue_depth: 0, retries: 0 }).collect();
    let gauge = Arc::new(AtomicUsize::new(0)); let max_buffers = (FANOUT_MAX_MEMORY / BLOCK).max(8);
    let state = Arc::new(JobState { running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false), files_total: AtomicU64::new(files.len() as u64), bytes_total: AtomicU64::new(bytes_total), buffers_in_flight: gauge, max_buffers, fanout: use_fanout, dests: Mutex::new(progress) });
    if use_fanout { let handles = fanout_job(source, dests, files, Arc::clone(&state), opts); let st = Arc::clone(&state); let watcher = thread::spawn(move || { for h in handles { let _ = h.join(); } st.running.store(false, Ordering::Release); }); Ok((state, vec![watcher])) }
    else { let n = dests.len() as u64; let done = Arc::new(AtomicU64::new(0)); let mut handles = Vec::new(); for (slot, dest) in dests.into_iter().enumerate() { let src = source.clone(); let list = Arc::clone(&files); let st = Arc::clone(&state); let counter = Arc::clone(&done); handles.push(thread::spawn(move || { per_dest_worker(src, dest, list, Arc::clone(&st), slot, opts, CopyMode::PerDestination); if counter.fetch_add(1, Ordering::AcqRel) + 1 >= n { st.running.store(false, Ordering::Release); } })); } Ok((state, handles)) }
}

pub fn format_bps(bps: f64) -> String { if bps >= 1_073_741_824.0 { format!("{:.2} GB/s", bps / 1_073_741_824.0) } else if bps >= 1_048_576.0 { format!("{:.1} MB/s", bps / 1_048_576.0) } else if bps >= 1024.0 { format!("{:.0} KB/s", bps / 1024.0) } else { format!("{:.0} B/s", bps) } }

#[cfg(test)]
mod tests {
    use super::*; use std::time::{SystemTime, UNIX_EPOCH};
    fn temp_dir(name: &str) -> PathBuf { let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos(); let p = std::env::temp_dir().join(format!("disk-duplicator-{name}-{stamp}")); fs::create_dir_all(&p).unwrap(); p }
    #[test] fn state_key_is_stable() { let a = FileInfo { rel: PathBuf::from("a/b.txt"), size: 42, mtime_ns: 7 }; assert_eq!(state_key(&a), state_key(&a.clone())); assert_ne!(state_key(&a), state_key(&FileInfo { size: 43, ..a })); }
    #[test] fn atomic_copy_verifies() { let root = temp_dir("atomic"); let src = root.join("src.bin"); let dst = root.join("dst.bin"); fs::write(&src, b"new-data").unwrap(); fs::write(&dst, b"old-data").unwrap(); copy_file_atomic(&src, &dst, true).unwrap(); assert_eq!(fs::read(&dst).unwrap(), b"new-data"); let _ = fs::remove_dir_all(root); }
    #[test] fn budget_never_exceeds_limit() { let gauge = Arc::new(AtomicUsize::new(0)); let budget = BufferBudget::new(2, Arc::clone(&gauge)); let state = JobState { running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false), files_total: AtomicU64::new(0), bytes_total: AtomicU64::new(0), buffers_in_flight: gauge, max_buffers: 2, fanout: true, dests: Mutex::new(Vec::new()) }; budget.acquire(&state); let a = Arc::new(Buffer { data: vec![1].into_boxed_slice(), budget: Arc::clone(&budget) }); budget.acquire(&state); let b = Arc::new(Buffer { data: vec![2].into_boxed_slice(), budget: Arc::clone(&budget) }); assert_eq!(state.buffers_in_flight.load(Ordering::Relaxed), 2); drop(a); drop(b); assert_eq!(state.buffers_in_flight.load(Ordering::Relaxed), 0); }
    #[test] fn format_is_sane() { assert_eq!(format_bps(0.0), "0 B/s"); assert!(format_bps(1024.0).contains("KB/s")); }
}
