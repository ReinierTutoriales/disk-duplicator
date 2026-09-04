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
pub struct CopyOpts {
    pub verify: bool,
    pub skip_same: bool,
    pub keep_going: bool,
}

#[derive(Clone, Copy, PartialEq, Eq)]
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

    fn acquire(&self) {
        let mut n = self.in_flight.lock().unwrap();
        while *n >= self.max {
            n = self.cv.wait(n).unwrap();
        }
        *n += 1;
        self.gauge.store(*n, Ordering::Relaxed);
    }

    fn release(&self) {
        let mut n = self.in_flight.lock().unwrap();
        *n = n.saturating_sub(1);
        self.gauge.store(*n, Ordering::Relaxed);
        self.cv.notify_one();
    }
}

struct Buffer {
    data: Box<[u8]>,
    budget: Arc<BufferBudget>,
}

type BufferRef = Arc<Buffer>;

impl Drop for Buffer {
    fn drop(&mut self) {
        self.budget.release();
    }
}

#[derive(Clone)]
struct FileInfo {
    rel: PathBuf,
    size: u64,
    mtime_ns: u128,
}

enum FanoutItem {
    Begin(FileInfo),
    Data(BufferRef),
    End,
}

struct DestControl {
    degraded: AtomicBool,
    queue_depth: AtomicUsize,
}

fn list_files(root: &Path) -> Result<Vec<FileInfo>, String> {
    let mut out = Vec::new();
    for e in WalkDir::new(root).follow_links(false) {
        let e = e.map_err(|err| err.to_string())?;
        if !e.file_type().is_file() {
            continue;
        }
        let meta = e.metadata().map_err(|err| err.to_string())?;
        let rel = e.path().strip_prefix(root).map_err(|err| err.to_string())?.to_path_buf();
        let mtime_ns = meta.modified().ok()
            .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|d| d.as_nanos()).unwrap_or(0);
        out.push(FileInfo { rel, size: meta.len(), mtime_ns });
    }
    Ok(out)
}

fn dest_inside_source(src: &Path, dst: &Path) -> bool {
    let Ok(s) = src.canonicalize() else { return false; };
    let Ok(d) = dst.canonicalize() else { return false; };
    d == s || d.starts_with(&s)
}

fn wait_pause(state: &JobState) {
    while state.pause.load(Ordering::Relaxed) && !state.cancel.load(Ordering::Relaxed) {
        thread::sleep(Duration::from_millis(40));
    }
}

fn set_phase(state: &JobState, slot: usize, phase: DestPhase, err: Option<String>) {
    let mut g = state.dests.lock().unwrap();
    g[slot].phase = phase;
    g[slot].error = err;
}

fn same_enough(src: &Path, dst: &Path) -> bool {
    let Ok(a) = fs::metadata(src) else { return false; };
    let Ok(b) = fs::metadata(dst) else { return false; };
    if a.len() != b.len() { return false; }
    match (a.modified(), b.modified()) { (Ok(x), Ok(y)) => x == y, _ => true }
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
    text.lines().filter_map(|line| {
        let (_, rest) = line.split_once("\"key\":\"")?;
        let (key, _) = rest.split_once("\"}")?;
        Some(key.to_owned())
    }).collect()
}

fn append_state(dest: &Path, key: &str) -> Result<(), String> {
    fs::create_dir_all(state_dir(dest)).map_err(|e| format!("state mkdir: {e}"))?;
    let mut f = OpenOptions::new().create(true).append(true).open(state_path(dest))
        .map_err(|e| format!("state open: {e}"))?;
    writeln!(f, r#"{{"key":"{}"}}"#, key).map_err(|e| format!("state write: {e}"))?;
    f.sync_all().map_err(|e| format!("state sync: {e}"))?;
    Ok(())
}

fn temp_path(dst: &Path) -> PathBuf {
    let name = dst.file_name().and_then(|n| n.to_str()).unwrap_or("file");
    dst.with_file_name(format!(".{name}.disk-duplicator.part"))
}

fn commit_temp(temp: &Path, dst: &Path) -> Result<(), String> {
    if let Some(parent) = dst.parent() { fs::create_dir_all(parent).map_err(|e| format!("mkdir: {e}"))?; }
    if dst.exists() { fs::remove_file(dst).map_err(|e| format!("replace: {e}"))?; }
    fs::rename(temp, dst).map_err(|e| format!("rename: {e}"))?;
    Ok(())
}

fn copy_file_atomic(src: &Path, dst: &Path, verify: bool) -> Result<(), String> {
    if let Some(parent) = dst.parent() { fs::create_dir_all(parent).map_err(|e| format!("mkdir: {e}"))?; }
    let meta = fs::metadata(src).map_err(|e| format!("metadata: {e}"))?;
    let mut inn = File::open(src).map_err(|e| format!("abrir origen: {e}"))?;
    let tmp = temp_path(dst);
    let _ = fs::remove_file(&tmp);
    let mut out = File::create(&tmp).map_err(|e| format!("crear temporal: {e}"))?;
    let mut buf = vec![0u8; BLOCK];
    let mut hasher = blake3::Hasher::new();
    loop {
        let n = inn.read(&mut buf).map_err(|e| format!("lectura: {e}"))?;
        if n == 0 { break; }
        out.write_all(&buf[..n]).map_err(|e| format!("escritura: {e}"))?;
        hasher.update(&buf[..n]);
    }
    out.flush().map_err(|e| format!("flush: {e}"))?;
    out.sync_all().map_err(|e| format!("sync: {e}"))?;
    drop(out);
    drop(inn);
    commit_temp(&tmp, dst)?;
    if verify {
        let expect = hasher.finalize();
        let mut f = File::open(dst).map_err(|e| format!("verificar: {e}"))?;
        let mut check = blake3::Hasher::new();
        loop {
            let n = f.read(&mut buf).map_err(|e| format!("verificar: {e}"))?;
            if n == 0 { break; }
            check.update(&buf[..n]);
        }
        if check.finalize() != expect {
            let _ = fs::remove_file(dst);
            return Err(format!("BLAKE3 no coincide: {}", dst.display()));
        }
    }
    if let Ok(m) = meta.modified() {
        if let Ok(mut f) = OpenOptions::new().write(true).open(dst) {
            let _ = f.set_modified(m);
            let _ = f.sync_all();
        }
    }
    Ok(())
}

fn update_written(state: &JobState, slot: usize, size: u64, start: Instant) {
    let mut g = state.dests.lock().unwrap();
    g[slot].written += size;
    g[slot].files_done += 1;
    let secs = start.elapsed().as_secs_f64();
    if secs > 0.05 { g[slot].bps = g[slot].written as f64 / secs; }
}

fn per_dest_worker(source: PathBuf, dest: PathBuf, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, slot: usize, opts: CopyOpts, mode: CopyMode) {
    let start = Instant::now();
    {
        let mut g = state.dests.lock().unwrap();
        g[slot].mode = mode;
        g[slot].phase = DestPhase::Copying;
    }
    let completed = load_state(&dest);
    for info in files.iter() {
        if state.cancel.load(Ordering::Relaxed) { set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into())); return; }
        wait_pause(&state);
        state.dests.lock().unwrap()[slot].last_file = info.rel.to_string_lossy().into_owned();
        let key = state_key(info);
        let src = source.join(&info.rel);
        let dst = dest.join(&info.rel);
        if completed.contains(&key) || (opts.skip_same && same_enough(&src, &dst)) {
            let mut g = state.dests.lock().unwrap();
            g[slot].files_skip += 1;
            g[slot].files_done += 1;
            g[slot].written += info.size;
            continue;
        }
        let mut ok = false;
        for attempt in 0..=RETRIES {
            match copy_file_atomic(&src, &dst, opts.verify) {
                Ok(()) => { ok = true; break; }
                Err(e) => {
                    if attempt < RETRIES {
                        state.dests.lock().unwrap()[slot].retries += 1;
                        thread::sleep(Duration::from_millis(50 * (attempt as u64 + 1)));
                    } else {
                        let mut g = state.dests.lock().unwrap();
                        g[slot].files_err += 1;
                        g[slot].error = Some(format!("{}: {e}", info.rel.display()));
                    }
                }
            }
        }
        if !ok {
            if !opts.keep_going { set_phase(&state, slot, DestPhase::Failed, None); return; }
            continue;
        }
        if let Err(e) = append_state(&dest, &key) {
            state.dests.lock().unwrap()[slot].error = Some(e);
        }
        update_written(&state, slot, info.size, start);
    }
    let errs = state.dests.lock().unwrap()[slot].files_err;
    if errs > 0 { set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es)."))); }
    else { set_phase(&state, slot, DestPhase::Done, None); }
}

fn fanout_worker(source: PathBuf, dest: PathBuf, rx: mpsc::Receiver<FanoutItem>, control: Arc<DestControl>, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, slot: usize, opts: CopyOpts) {
    let start = Instant::now();
    let mut current: Option<(FileInfo, File, blake3::Hasher)> = None;
    set_phase(&state, slot, DestPhase::Copying, None);
    loop {
        if control.degraded.load(Ordering::Relaxed) {
            if let Some((info, mut f, _)) = current.take() {
                let _ = f.flush();
                let _ = f.sync_all();
                let _ = fs::remove_file(temp_path(&dest.join(&info.rel)));
            }
            per_dest_worker(source, dest, files, state, slot, opts, CopyMode::Fallback);
            return;
        }
        match rx.recv() {
            Ok(FanoutItem::Begin(info)) => {
                let dst = dest.join(&info.rel);
                if let Some(parent) = dst.parent() {
                    if let Err(e) = fs::create_dir_all(parent) {
                        control.degraded.store(true, Ordering::Relaxed);
                        set_phase(&state, slot, DestPhase::Failed, Some(format!("mkdir: {e}")));
                        continue;
                    }
                }
                let tmp = temp_path(&dst);
                let _ = fs::remove_file(&tmp);
                match File::create(&tmp) {
                    Ok(f) => current = Some((info, f, blake3::Hasher::new())),
                    Err(e) => {
                        control.degraded.store(true, Ordering::Relaxed);
                        set_phase(&state, slot, DestPhase::Failed, Some(format!("temporal: {e}")));
                    }
                }
            }
            Ok(FanoutItem::Data(buf)) => {
                if let Some((_, f, hasher)) = current.as_mut() {
                    if let Err(e) = f.write_all(&buf.data) {
                        control.degraded.store(true, Ordering::Relaxed);
                        set_phase(&state, slot, DestPhase::Failed, Some(format!("escritura: {e}")));
                    } else {
                        hasher.update(&buf.data);
                    }
                }
                control.queue_depth.fetch_sub(1, Ordering::Relaxed);
                state.dests.lock().unwrap()[slot].queue_depth = control.queue_depth.load(Ordering::Relaxed);
            }
            Ok(FanoutItem::End) => {
                let Some((info, mut f, hasher)) = current.take() else { continue; };
                if let Err(e) = f.flush().and_then(|_| f.sync_all()) {
                    control.degraded.store(true, Ordering::Relaxed);
                    set_phase(&state, slot, DestPhase::Failed, Some(format!("sync: {e}")));
                    continue;
                }
                drop(f);
                let dst = dest.join(&info.rel);
                let tmp = temp_path(&dst);
                if let Err(e) = commit_temp(&tmp, &dst) {
                    control.degraded.store(true, Ordering::Relaxed);
                    set_phase(&state, slot, DestPhase::Failed, Some(e));
                    continue;
                }
                if opts.verify {
                    set_phase(&state, slot, DestPhase::Verifying, None);
                    let expect = hasher.finalize();
                    let mut f = match File::open(&dst) {
                        Ok(f) => f,
                        Err(e) => { control.degraded.store(true, Ordering::Relaxed); set_phase(&state, slot, DestPhase::Failed, Some(format!("verificar: {e}"))); continue; }
                    };
                    let mut check = blake3::Hasher::new();
                    let mut buf = vec![0u8; BLOCK];
                    let mut bad = None;
                    loop {
                        match f.read(&mut buf) {
                            Ok(0) => break,
                            Ok(n) => check.update(&buf[..n]),
                            Err(e) => { bad = Some(e.to_string()); break; }
                        }
                    }
                    if bad.is_some() || check.finalize() != expect {
                        let _ = fs::remove_file(&dst);
                        control.degraded.store(true, Ordering::Relaxed);
                        set_phase(&state, slot, DestPhase::Failed, Some(format!("BLAKE3 no coincide: {}", dst.display())));
                        continue;
                    }
                }
                if let Ok(m) = fs::metadata(source.join(&info.rel)).and_then(|m| m.modified()) {
                    if let Ok(mut f) = OpenOptions::new().write(true).open(&dst) {
                        let _ = f.set_modified(m);
                        let _ = f.sync_all();
                    }
                }
                if let Err(e) = append_state(&dest, &state_key(&info)) {
                    state.dests.lock().unwrap()[slot].error = Some(e);
                }
                update_written(&state, slot, info.size, start);
                set_phase(&state, slot, DestPhase::Copying, None);
            }
            Err(_) => {
                if control.degraded.load(Ordering::Relaxed) {
                    per_dest_worker(source, dest, files, state, slot, opts, CopyMode::Fallback);
                }
                return;
            }
        }
    }
}

fn send_with_backpressure(tx: &mpsc::SyncSender<FanoutItem>, item: FanoutItem, control: &DestControl) -> Result<(), ()> {
    let started = Instant::now();
    let mut item = Some(item);
    loop {
        if control.degraded.load(Ordering::Relaxed) { return Err(()); }
        match tx.try_send(item.take().unwrap()) {
            Ok(()) => return Ok(()),
            Err(mpsc::TrySendError::Full(v)) => {
                item = Some(v);
                if started.elapsed() >= FANOUT_DEGRADE_AFTER {
                    control.degraded.store(true, Ordering::Relaxed);
                    return Err(());
                }
                thread::sleep(Duration::from_millis(2));
            }
            Err(mpsc::TrySendError::Disconnected(_)) => {
                control.degraded.store(true, Ordering::Relaxed);
                return Err(());
            }
        }
    }
}

fn fanout_job(source: PathBuf, dests: Vec<PathBuf>, files: Arc<Vec<FileInfo>>, state: Arc<JobState>, opts: CopyOpts) -> Vec<JoinHandle<()>> {
    let max_buffers = (FANOUT_MAX_MEMORY / BLOCK).max(8);
    let budget = BufferBudget::new(max_buffers, Arc::clone(&state.buffers_in_flight));
    let mut senders = Vec::new();
    let mut controls = Vec::new();
    let mut handles = Vec::new();

    for (slot, dest) in dests.iter().cloned().enumerate() {
        let (tx, rx) = mpsc::sync_channel(FANOUT_QUEUE);
        let control = Arc::new(DestControl { degraded: AtomicBool::new(false), queue_depth: AtomicUsize::new(0) });
        controls.push(Arc::clone(&control));
        let src = source.clone();
        let list = Arc::clone(&files);
        let st = Arc::clone(&state);
        let c = Arc::clone(&control);
        handles.push(thread::spawn(move || fanout_worker(src, dest, rx, c, list, st, slot, opts)));
        senders.push(Some(tx));
    }

    let reader = thread::spawn(move || {
        let state_cache: Vec<HashSet<String>> = dests.iter().map(|d| load_state(d)).collect();
        for info in files.iter() {
            if state.cancel.load(Ordering::Relaxed) { break; }
            wait_pause(&state);
            let key = state_key(info);
            let mut targets = 0usize;
            for slot in 0..dests.len() {
                let control = &controls[slot];
                if control.degraded.load(Ordering::Relaxed) || state_cache[slot].contains(&key) { continue; }
                let src = source.join(&info.rel);
                let dst = dests[slot].join(&info.rel);
                if opts.skip_same && same_enough(&src, &dst) { continue; }
                let result = senders[slot].as_ref().map(|tx| send_with_backpressure(tx, FanoutItem::Begin(info.clone()), control));
                if result == Some(Ok(())) { targets += 1; }
                else if result == Some(Err(())) { senders[slot] = None; }
            }
            if targets == 0 { continue; }

            let src_path = source.join(&info.rel);
            let mut input = match File::open(&src_path) {
                Ok(f) => f,
                Err(e) => {
                    for control in &controls { control.degraded.store(true, Ordering::Relaxed); }
                    for slot in 0..dests.len() { state.dests.lock().unwrap()[slot].error = Some(format!("{}: {e}", info.rel.display())); }
                    break;
                }
            };

            loop {
                if state.cancel.load(Ordering::Relaxed) { break; }
                wait_pause(&state);
                budget.acquire();
                let mut data = vec![0u8; BLOCK];
                let n = match input.read(&mut data) {
                    Ok(n) => n,
                    Err(e) => {
                        for control in &controls { control.degraded.store(true, Ordering::Relaxed); }
                        for slot in 0..dests.len() { state.dests.lock().unwrap()[slot].error = Some(format!("lectura {}: {e}", info.rel.display())); }
                        0
                    }
                };
                if n == 0 {
                    let empty = Buffer { data: Vec::new().into_boxed_slice(), budget: Arc::clone(&budget) };
                    drop(empty);
                    break;
                }
                data.truncate(n);
                let buffer = Arc::new(Buffer { data: data.into_boxed_slice(), budget: Arc::clone(&budget) });
                let mut delivered = 0usize;
                for slot in 0..dests.len() {
                    let control = &controls[slot];
                    if control.degraded.load(Ordering::Relaxed) { continue; }
                    let reserved = control.queue_depth.fetch_add(1, Ordering::Relaxed) + 1;
                    state.dests.lock().unwrap()[slot].queue_depth = reserved;
                    let result = senders[slot].as_ref().map(|tx| send_with_backpressure(tx, FanoutItem::Data(Arc::clone(&buffer)), control));
                    if result == Some(Ok(())) { delivered += 1; }
                    else {
                        control.queue_depth.fetch_sub(1, Ordering::Relaxed);
                        state.dests.lock().unwrap()[slot].queue_depth = control.queue_depth.load(Ordering::Relaxed);
                        if result == Some(Err(())) { senders[slot] = None; }
                    }
                }
                drop(buffer);
                if delivered == 0 { break; }
            }

            for slot in 0..dests.len() {
                if controls[slot].degraded.load(Ordering::Relaxed) { continue; }
                let result = senders[slot].as_ref().map(|tx| send_with_backpressure(tx, FanoutItem::End, &controls[slot]));
                if result == Some(Err(())) { senders[slot] = None; }
            }
        }
        drop(senders);
    });
    handles.push(reader);
    handles
}

pub fn start_job(source: PathBuf, dests: Vec<PathBuf>, opts: CopyOpts) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    if !source.is_dir() { return Err("El origen debe ser una carpeta.".into()); }
    if dests.is_empty() { return Err("Agrega al menos un destino.".into()); }
    for d in &dests {
        if dest_inside_source(&source, d) { return Err(format!("El destino {} está dentro del origen.", d.display())); }
        fs::create_dir_all(d).map_err(|e| format!("destino {}: {e}", d.display()))?;
    }
    let files = Arc::new(list_files(&source)?);
    if files.is_empty() { return Err("El origen no tiene archivos.".into()); }
    let bytes_total: u64 = files.iter().map(|f| f.size).sum();
    let files_total = files.len() as u64;
    let use_fanout = dests.len() > 1 && dests.len() <= FANOUT_MAX_DESTS;
    let progress = dests.iter().map(|d| DestProgress {
        label: d.display().to_string(), written: 0, total: bytes_total, files_done: 0, files_skip: 0,
        files_err: 0, bps: 0.0, phase: DestPhase::Idle, error: None, last_file: String::new(),
        mode: if use_fanout { CopyMode::Fanout } else { CopyMode::PerDestination }, queue_depth: 0, retries: 0,
    }).collect();
    let gauge = Arc::new(AtomicUsize::new(0));
    let max_buffers = (FANOUT_MAX_MEMORY / BLOCK).max(8);
    let state = Arc::new(JobState {
        running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false),
        files_total: AtomicU64::new(files_total), bytes_total: AtomicU64::new(bytes_total),
        buffers_in_flight: gauge, max_buffers, fanout: use_fanout, dests: Mutex::new(progress),
    });
    if use_fanout {
        let handles = fanout_job(source, dests, files, Arc::clone(&state), opts);
        let st = Arc::clone(&state);
        let watcher = thread::spawn(move || { for h in handles { let _ = h.join(); } st.running.store(false, Ordering::Relaxed); });
        Ok((state, vec![watcher]))
    } else {
        let n = dests.len() as u64;
        let done = Arc::new(AtomicU64::new(0));
        let mut handles = Vec::new();
        for (slot, dest) in dests.into_iter().enumerate() {
            let src = source.clone(); let list = Arc::clone(&files); let st = Arc::clone(&state); let counter = Arc::clone(&done);
            handles.push(thread::spawn(move || {
                per_dest_worker(src, dest, list, Arc::clone(&st), slot, opts, CopyMode::PerDestination);
                if counter.fetch_add(1, Ordering::Relaxed) + 1 >= n { st.running.store(false, Ordering::Relaxed); }
            }));
        }
        Ok((state, handles))
    }
}

pub fn format_bps(bps: f64) -> String {
    if bps >= 1_073_741_824.0 { format!("{:.2} GB/s", bps / 1_073_741_824.0) }
    else if bps >= 1_048_576.0 { format!("{:.1} MB/s", bps / 1_048_576.0) }
    else if bps >= 1024.0 { format!("{:.0} KB/s", bps / 1024.0) }
    else { format!("{:.0} B/s", bps) }
}

pub fn format_bytes(n: u64) -> String {
    if n >= 1_073_741_824 { format!("{:.2} GB", n as f64 / 1_073_741_824.0) }
    else if n >= 1_048_576 { format!("{:.1} MB", n as f64 / 1_048_576.0) }
    else if n >= 1024 { format!("{:.0} KB", n as f64 / 1024.0) }
    else { format!("{n} B") }
}
