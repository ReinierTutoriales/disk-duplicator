use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Instant;
use walkdir::WalkDir;

const BLOCK: usize = 4 * 1024 * 1024;

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

fn wait_pause(state: &JobState) {
    while state.pause.load(Ordering::Relaxed) && !state.cancel.load(Ordering::Relaxed) {
        thread::sleep(std::time::Duration::from_millis(40));
    }
}

fn set_phase(state: &JobState, slot: usize, phase: DestPhase, err: Option<String>) {
    let mut g = state.dests.lock().unwrap();
    g[slot].phase = phase;
    g[slot].error = err;
}

fn same_enough(src: &Path, dst: &Path) -> bool {
    let Ok(a) = fs::metadata(src) else {
        return false;
    };
    let Ok(b) = fs::metadata(dst) else {
        return false;
    };
    if a.len() != b.len() {
        return false;
    }
    match (a.modified(), b.modified()) {
        (Ok(x), Ok(y)) => x == y,
        _ => true,
    }
}

fn copy_file(src: &Path, dst: &Path, verify: bool) -> Result<(), String> {
    if let Some(parent) = dst.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("mkdir: {e}"))?;
    }
    let meta = fs::metadata(src).ok();
    let mut inn = File::open(src).map_err(|e| format!("abrir origen: {e}"))?;
    let mut out = File::create(dst).map_err(|e| format!("crear destino: {e}"))?;
    let mut buf = vec![0u8; BLOCK];
    let mut hasher = blake3::Hasher::new();
    loop {
        let n = inn.read(&mut buf).map_err(|e| format!("lectura: {e}"))?;
        if n == 0 {
            break;
        }
        out.write_all(&buf[..n]).map_err(|e| format!("escritura: {e}"))?;
        hasher.update(&buf[..n]);
    }
    out.flush().map_err(|e| format!("flush: {e}"))?;
    if let Some(m) = meta.as_ref().and_then(|m| m.modified().ok()) {
        let _ = out.set_modified(m);
    }
    drop(out);
    drop(inn);
    if verify {
        let expect = hasher.finalize();
        let mut f = File::open(dst).map_err(|e| format!("verificar: {e}"))?;
        let mut check = blake3::Hasher::new();
        loop {
            let n = f.read(&mut buf).map_err(|e| format!("verificar: {e}"))?;
            if n == 0 {
                break;
            }
            check.update(&buf[..n]);
        }
        if check.finalize() != expect {
            return Err(format!("BLAKE3 no coincide: {}", dst.display()));
        }
    }
    Ok(())
}

fn dest_worker(
    source: PathBuf,
    dest: PathBuf,
    files: Arc<Vec<(PathBuf, u64)>>,
    state: Arc<JobState>,
    slot: usize,
    opts: CopyOpts,
) {
    let t0 = Instant::now();
    set_phase(&state, slot, DestPhase::Copying, None);
    for (rel, size) in files.iter() {
        if state.cancel.load(Ordering::Relaxed) {
            set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
            return;
        }
        wait_pause(&state);
        state.dests.lock().unwrap()[slot].last_file = rel.to_string_lossy().into_owned();
        let src = source.join(rel);
        let dst = dest.join(rel);
        if opts.skip_same && same_enough(&src, &dst) {
            let mut g = state.dests.lock().unwrap();
            g[slot].files_skip += 1;
            g[slot].files_done += 1;
            g[slot].written += *size;
            continue;
        }
        if let Err(e) = copy_file(&src, &dst, opts.verify) {
            let mut g = state.dests.lock().unwrap();
            g[slot].files_err += 1;
            g[slot].error = Some(format!("{}: {e}", rel.display()));
            if !opts.keep_going {
                g[slot].phase = DestPhase::Failed;
                return;
            }
            continue;
        }
        {
            let mut g = state.dests.lock().unwrap();
            g[slot].written += *size;
            g[slot].files_done += 1;
            let s = t0.elapsed().as_secs_f64();
            if s > 0.05 {
                g[slot].bps = g[slot].written as f64 / s;
            }
        }
    }
    let errs = state.dests.lock().unwrap()[slot].files_err;
    if errs > 0 {
        set_phase(
            &state,
            slot,
            DestPhase::Done,
            Some(format!("Terminado con {errs} error(es).")),
        );
    } else {
        set_phase(&state, slot, DestPhase::Done, None);
    }
}

pub fn start_job(
    source: PathBuf,
    dests: Vec<PathBuf>,
    opts: CopyOpts,
) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    if !source.is_dir() {
        return Err("El origen debe ser una carpeta.".into());
    }
    if dests.is_empty() {
        return Err("Agrega al menos un destino.".into());
    }
    for d in &dests {
        if dest_inside_source(&source, d) {
            return Err(format!("El destino {} está dentro del origen.", d.display()));
        }
        fs::create_dir_all(d).map_err(|e| format!("destino {}: {e}", d.display()))?;
    }
    let files = Arc::new(list_files(&source)?);
    if files.is_empty() {
        return Err("El origen no tiene archivos.".into());
    }
    let bytes_total: u64 = files.iter().map(|(_, n)| *n).sum();
    let files_total = files.len() as u64;
    let progress = dests
        .iter()
        .map(|d| DestProgress {
            label: d.display().to_string(),
            written: 0,
            total: bytes_total,
            files_done: 0,
            files_skip: 0,
            files_err: 0,
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
    let n = dests.len() as u64;
    let done = Arc::new(AtomicU64::new(0));
    let mut handles = Vec::new();
    for (slot, dest) in dests.into_iter().enumerate() {
        let src = source.clone();
        let list = Arc::clone(&files);
        let st = Arc::clone(&state);
        let counter = Arc::clone(&done);
        handles.push(thread::spawn(move || {
            dest_worker(src, dest, list, Arc::clone(&st), slot, opts);
            if counter.fetch_add(1, Ordering::Relaxed) + 1 >= n {
                st.running.store(false, Ordering::Relaxed);
            }
        }));
    }
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
