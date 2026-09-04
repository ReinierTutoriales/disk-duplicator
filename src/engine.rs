use crossbeam_channel::{bounded, Receiver, Sender, TrySendError};
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use walkdir::WalkDir;

const BLOCK: usize = 1024 * 1024;
const QUEUE: usize = 16;

#[derive(Clone)]
enum Msg {
    Begin {
        rel: PathBuf,
        size: u64,
    },
    Chunk(Arc<[u8]>),
    End {
        hash: [u8; 32],
        verify: bool,
    },
    Done,
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

#[derive(Clone)]
pub struct DestProgress {
    pub label: String,
    pub written: u64,
    pub total: u64,
    pub files_done: u64,
    pub bps: f64,
    pub phase: DestPhase,
    pub error: Option<String>,
    pub last_file: String,
}

pub struct JobState {
    pub running: AtomicBool,
    pub cancel: AtomicBool,
    pub pause: AtomicBool,
    pub files_total: AtomicU64,
    pub bytes_total: AtomicU64,
    pub dests: Mutex<Vec<DestProgress>>,
}

impl JobState {
    pub fn snapshot(&self) -> Vec<DestProgress> {
        self.dests.lock().unwrap().clone()
    }
}

fn list_files(root: &Path) -> Result<Vec<(PathBuf, u64)>, String> {
    let mut out = Vec::new();
    for e in WalkDir::new(root).follow_links(false) {
        let e = e.map_err(|err| err.to_string())?;
        if !e.file_type().is_file() {
            continue;
        }
        let meta = e.metadata().map_err(|err| err.to_string())?;
        let rel = e
            .path()
            .strip_prefix(root)
            .map_err(|err| err.to_string())?
            .to_path_buf();
        out.push((rel, meta.len()));
    }
    Ok(out)
}

fn dest_inside_source(src: &Path, dst: &Path) -> bool {
    let Ok(s) = src.canonicalize() else {
        return false;
    };
    let Ok(d) = dst.canonicalize() else {
        return false;
    };
    d == s || d.starts_with(&s)
}

fn wait_paused(state: &JobState) {
    while state.pause.load(Ordering::Relaxed) && !state.cancel.load(Ordering::Relaxed) {
        thread::sleep(Duration::from_millis(50));
    }
}

fn fanout(txs: &[Sender<Msg>], msg: Msg, state: &JobState) -> Result<(), String> {
    let mut pending: Vec<usize> = (0..txs.len()).collect();
    while !pending.is_empty() {
        if state.cancel.load(Ordering::Relaxed) {
            return Err("Cancelado".into());
        }
        wait_paused(state);
        pending.retain(|&i| match txs[i].try_send(msg.clone()) {
            Ok(()) => false,
            Err(TrySendError::Full(_)) => true,
            Err(TrySendError::Disconnected(_)) => false,
        });
        if !pending.is_empty() {
            thread::sleep(Duration::from_millis(1));
        }
    }
    Ok(())
}

fn writer_loop(rx: Receiver<Msg>, root: PathBuf, state: Arc<JobState>, slot: usize) {
    let t0 = Instant::now();
    let mut current: Option<File> = None;
    let mut current_path = PathBuf::new();
    let mut current_size = 0u64;
    loop {
        if state.cancel.load(Ordering::Relaxed) {
            set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
            return;
        }
        let Ok(msg) = rx.recv() else {
            set_phase(&state, slot, DestPhase::Failed, Some("Canal cerrado".into()));
            return;
        };
        match msg {
            Msg::Begin { rel, size } => {
                let path = root.join(&rel);
                if let Some(parent) = path.parent() {
                    if let Err(e) = fs::create_dir_all(parent) {
                        set_phase(&state, slot, DestPhase::Failed, Some(format!("mkdir: {e}")));
                        return;
                    }
                }
                match File::create(&path) {
                    Ok(f) => {
                        current = Some(f);
                        current_path = path;
                        current_size = size;
                        let mut g = state.dests.lock().unwrap();
                        g[slot].phase = DestPhase::Copying;
                        g[slot].last_file = rel.to_string_lossy().into_owned();
                        g[slot].error = None;
                    }
                    Err(e) => {
                        set_phase(&state, slot, DestPhase::Failed, Some(format!("crear: {e}")));
                        return;
                    }
                }
            }
            Msg::Chunk(data) => {
                let Some(f) = current.as_mut() else {
                    set_phase(&state, slot, DestPhase::Failed, Some("chunk sin archivo".into()));
                    return;
                };
                if let Err(e) = f.write_all(&data) {
                    set_phase(&state, slot, DestPhase::Failed, Some(format!("escritura: {e}")));
                    return;
                }
                let mut g = state.dests.lock().unwrap();
                g[slot].written += data.len() as u64;
                let elapsed = t0.elapsed().as_secs_f64();
                if elapsed > 0.05 {
                    g[slot].bps = g[slot].written as f64 / elapsed;
                }
            }
            Msg::End { hash, verify } => {
                if let Some(mut f) = current.take() {
                    let _ = f.flush();
                    drop(f);
                }
                if verify {
                    set_phase(&state, slot, DestPhase::Verifying, None);
                    match verify_file(&current_path, current_size, hash) {
                        Ok(()) => {}
                        Err(e) => {
                            set_phase(&state, slot, DestPhase::Failed, Some(e));
                            return;
                        }
                    }
                    set_phase(&state, slot, DestPhase::Copying, None);
                }
                state.dests.lock().unwrap()[slot].files_done += 1;
            }
            Msg::Done => {
                set_phase(&state, slot, DestPhase::Done, None);
                return;
            }
        }
    }
}

fn verify_file(path: &Path, size: u64, expect: [u8; 32]) -> Result<(), String> {
    let mut f = File::open(path).map_err(|e| format!("verificar open: {e}"))?;
    let mut hasher = blake3::Hasher::new();
    let mut buf = vec![0u8; BLOCK];
    let mut seen = 0u64;
    loop {
        let n = f.read(&mut buf).map_err(|e| format!("verificar read: {e}"))?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        seen += n as u64;
    }
    if seen != size {
        return Err(format!("tamaño {seen} != {size} en {}", path.display()));
    }
    let got = *hasher.finalize().as_bytes();
    if got != expect {
        return Err(format!("BLAKE3 no coincide en {}", path.display()));
    }
    Ok(())
}

fn set_phase(state: &JobState, slot: usize, phase: DestPhase, err: Option<String>) {
    let mut g = state.dests.lock().unwrap();
    g[slot].phase = phase;
    g[slot].error = err;
}

fn reader(
    source: PathBuf,
    files: Vec<(PathBuf, u64)>,
    txs: Vec<Sender<Msg>>,
    state: Arc<JobState>,
    verify: bool,
) {
    let mut buf = vec![0u8; BLOCK];
    for (rel, size) in files {
        if state.cancel.load(Ordering::Relaxed) {
            break;
        }
        wait_paused(&state);
        if fanout(
            &txs,
            Msg::Begin {
                rel: rel.clone(),
                size,
            },
            &state,
        )
        .is_err()
        {
            break;
        }
        let path = source.join(&rel);
        let mut f = match File::open(&path) {
            Ok(f) => f,
            Err(e) => {
                for i in 0..txs.len() {
                    set_phase(&state, i, DestPhase::Failed, Some(format!("origen: {e}")));
                }
                return;
            }
        };
        let mut hasher = blake3::Hasher::new();
        loop {
            if state.cancel.load(Ordering::Relaxed) {
                return;
            }
            wait_paused(&state);
            let n = match f.read(&mut buf) {
                Ok(0) => break,
                Ok(n) => n,
                Err(e) => {
                    for i in 0..txs.len() {
                        set_phase(&state, i, DestPhase::Failed, Some(format!("lectura: {e}")));
                    }
                    return;
                }
            };
            hasher.update(&buf[..n]);
            let chunk: Arc<[u8]> = Arc::from(&buf[..n]);
            if fanout(&txs, Msg::Chunk(chunk), &state).is_err() {
                return;
            }
        }
        let hash = *hasher.finalize().as_bytes();
        if fanout(&txs, Msg::End { hash, verify }, &state).is_err() {
            return;
        }
    }
    let _ = fanout(&txs, Msg::Done, &state);
}

pub fn start_job(
    source: PathBuf,
    dests: Vec<PathBuf>,
    verify: bool,
) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    if !source.is_dir() {
        return Err("El origen debe ser una carpeta.".into());
    }
    if dests.is_empty() {
        return Err("Agrega al menos un destino.".into());
    }
    for d in &dests {
        if dest_inside_source(&source, d) {
            return Err(format!(
                "El destino {} está dentro del origen.",
                d.display()
            ));
        }
        fs::create_dir_all(d).map_err(|e| format!("destino {}: {e}", d.display()))?;
    }
    let files = list_files(&source)?;
    let bytes_total: u64 = files.iter().map(|(_, n)| n).sum();
    let files_total = files.len() as u64;

    let progress = dests
        .iter()
        .map(|d| DestProgress {
            label: d.display().to_string(),
            written: 0,
            total: bytes_total,
            files_done: 0,
            bps: 0.0,
            phase: DestPhase::Idle,
            error: None,
            last_file: String::new(),
        })
        .collect();

    let state = Arc::new(JobState {
        running: AtomicBool::new(true),
        cancel: AtomicBool::new(false),
        pause: AtomicBool::new(false),
        files_total: AtomicU64::new(files_total),
        bytes_total: AtomicU64::new(bytes_total),
        dests: Mutex::new(progress),
    });

    let mut txs = Vec::new();
    let mut handles = Vec::new();
    for (slot, dest) in dests.into_iter().enumerate() {
        let (tx, rx) = bounded::<Msg>(QUEUE);
        txs.push(tx);
        let st = Arc::clone(&state);
        handles.push(thread::spawn(move || writer_loop(rx, dest, st, slot)));
    }

    let st = Arc::clone(&state);
    handles.push(thread::spawn(move || {
        reader(source, files, txs, Arc::clone(&st), verify);
        st.running.store(false, Ordering::Relaxed);
    }));
    Ok((state, handles))
}

pub fn format_bps(bps: f64) -> String {
    if bps >= 1_073_741_824.0 {
        format!("{:.2} GB/s", bps / 1_073_741_824.0)
    } else if bps >= 1_048_576.0 {
        format!("{:.1} MB/s", bps / 1_048_576.0)
    } else if bps >= 1024.0 {
        format!("{:.0} KB/s", bps / 1024.0)
    } else {
        format!("{:.0} B/s", bps)
    }
}

pub fn format_bytes(n: u64) -> String {
    if n >= 1_073_741_824 {
        format!("{:.2} GB", n as f64 / 1_073_741_824.0)
    } else if n >= 1_048_576 {
        format!("{:.1} MB", n as f64 / 1_048_576.0)
    } else if n >= 1024 {
        format!("{:.0} KB", n as f64 / 1024.0)
    } else {
        format!("{n} B")
    }
}
