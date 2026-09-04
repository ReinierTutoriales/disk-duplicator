use crate::disks::{
    ioctl, letters_for_disk, open_disk_strict, DiskInfo, RawHandle, FILE_FLAG_NO_BUFFERING,
    FILE_FLAG_WRITE_THROUGH,
};
use crate::winpath::{pcw, volume_path, wide_z};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Instant;
use windows::Win32::Foundation::HANDLE;
use windows::Win32::Storage::FileSystem::{
    CreateFileW, ReadFile, SetFilePointerEx, WriteFile, FILE_ATTRIBUTE_NORMAL, FILE_BEGIN,
    FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows::Win32::System::Ioctl::{FSCTL_DISMOUNT_VOLUME, FSCTL_LOCK_VOLUME};

const BLOCK: usize = 1024 * 1024;
const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;
const FSCTL_ALLOW_EXTENDED_DASD_IO: u32 = 0x0009_0083;

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum DestPhase {
    Idle,
    Locking,
    Copying,
    Verifying,
    Done,
    Failed,
    Cancelled,
}

#[derive(Clone)]
pub struct DestProgress {
    pub index: u32,
    pub written: u64,
    pub verified: u64,
    pub total: u64,
    pub bps: f64,
    pub phase: DestPhase,
    pub error: Option<String>,
}

pub struct JobState {
    pub running: AtomicBool,
    pub cancel: AtomicBool,
    pub dests: Mutex<Vec<DestProgress>>,
}

impl JobState {
    pub fn snapshot(&self) -> Vec<DestProgress> {
        self.dests.lock().unwrap().clone()
    }
}

fn win_err(prefix: &str) -> String {
    let e = windows::core::Error::from_win32();
    format!("{prefix}: {e}")
}

fn open_volume(letter: char) -> Result<RawHandle, String> {
    let path = volume_path(letter);
    let w = wide_z(&path);
    unsafe {
        CreateFileW(
            pcw(&w),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            None,
        )
    }
    .map(RawHandle)
    .map_err(|e| format!("volumen {letter}: {e}"))
}

fn ctl(h: HANDLE, code: u32) -> bool {
    ioctl(h, code, None, 0, None, 0)
}

/// Bloquea y desmonta. Si hay letras y alguna falla, no se escribe ese destino.
/// Los handles viven hasta que `copy_one` termina — soltarlos libera el lock.
fn lock_volumes(index: u32, known: &[char]) -> Result<Vec<RawHandle>, String> {
    let mut letters = known.to_vec();
    for l in letters_for_disk(index) {
        if !letters.contains(&l) {
            letters.push(l);
        }
    }
    let mut held = Vec::new();
    for letter in letters {
        let h = open_volume(letter)?;
        let _ = ctl(h.0, FSCTL_ALLOW_EXTENDED_DASD_IO);
        let _ = ctl(h.0, FSCTL_DISMOUNT_VOLUME);
        if !ctl(h.0, FSCTL_LOCK_VOLUME) {
            let _ = ctl(h.0, FSCTL_DISMOUNT_VOLUME);
            if !ctl(h.0, FSCTL_LOCK_VOLUME) {
                return Err(format!(
                    "No se pudo bloquear {letter}:. Cierra Explorer y programas que usen ese disco. No se escribió nada."
                ));
            }
        }
        held.push(h);
    }
    Ok(held)
}

fn seek0(h: HANDLE) -> Result<(), String> {
    unsafe { SetFilePointerEx(h, 0, None, FILE_BEGIN) }.map_err(|e| format!("seek: {e}"))
}

fn read_block(h: HANDLE, buf: &mut [u8]) -> Result<usize, String> {
    let mut n = 0u32;
    unsafe { ReadFile(h, Some(buf), Some(&mut n), None) }.map_err(|_| win_err("lectura"))?;
    Ok(n as usize)
}

fn write_block(h: HANDLE, buf: &[u8]) -> Result<(), String> {
    let mut off = 0usize;
    while off < buf.len() {
        let mut n = 0u32;
        unsafe { WriteFile(h, Some(&buf[off..]), Some(&mut n), None) }
            .map_err(|_| win_err("escritura"))?;
        if n == 0 {
            return Err("escritura: 0 bytes (disco lleno o handle inválido)".into());
        }
        off += n as usize;
    }
    Ok(())
}

fn set_phase(state: &JobState, slot: usize, phase: DestPhase, err: Option<String>) {
    let mut g = state.dests.lock().unwrap();
    g[slot].phase = phase;
    g[slot].error = err;
}

fn add_written(state: &JobState, slot: usize, n: u64, elapsed: f64) {
    let mut g = state.dests.lock().unwrap();
    g[slot].written += n;
    if elapsed > 0.05 {
        g[slot].bps = g[slot].written as f64 / elapsed;
    }
}

fn add_verified(state: &JobState, slot: usize, n: u64) {
    state.dests.lock().unwrap()[slot].verified += n;
}

fn cancelled(state: &JobState, slot: usize) -> bool {
    if state.cancel.load(Ordering::Relaxed) {
        set_phase(state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
        true
    } else {
        false
    }
}

fn copy_one(
    source: DiskInfo,
    dest: DiskInfo,
    state: Arc<JobState>,
    slot: usize,
    verify: bool,
) -> Result<(), String> {
    if dest.is_system {
        return Err("destino es disco de sistema".into());
    }
    if dest.index == source.index {
        return Err("origen y destino son el mismo disco".into());
    }
    if dest.size_bytes < source.size_bytes {
        return Err("destino más pequeño que el origen".into());
    }

    set_phase(&state, slot, DestPhase::Locking, None);
    let _locks = lock_volumes(dest.index, &dest.letters)?;

    let src = open_disk_strict(&source.path, false, FILE_FLAG_NO_BUFFERING)?;
    let dst = open_disk_strict(
        &dest.path,
        true,
        FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
    )?;

    seek0(src.0)?;
    seek0(dst.0)?;
    set_phase(&state, slot, DestPhase::Copying, None);

    let mut buf = vec![0u8; BLOCK];
    let mut hasher = crc32fast::Hasher::new();
    let mut copied = 0u64;
    let total = source.size_bytes;
    let t0 = Instant::now();

    while copied < total {
        if cancelled(&state, slot) {
            return Err("Cancelado".into());
        }
        let want = ((total - copied) as usize).min(BLOCK);
        // NO_BUFFERING exige múltiplo de sector; el tamaño del disco lo es.
        let n = read_block(src.0, &mut buf[..want])?;
        if n == 0 {
            return Err(format!("lectura corta en offset {copied}"));
        }
        write_block(dst.0, &buf[..n])?;
        hasher.update(&buf[..n]);
        copied += n as u64;
        add_written(&state, slot, n as u64, t0.elapsed().as_secs_f64());
    }
    if copied != total {
        return Err(format!("incompleto: {copied} de {total} bytes. Destino no es una copia fiel."));
    }

    if verify {
        set_phase(&state, slot, DestPhase::Verifying, None);
        seek0(dst.0)?;
        let expect = hasher.finalize();
        let mut check = crc32fast::Hasher::new();
        let mut seen = 0u64;
        while seen < total {
            if cancelled(&state, slot) {
                return Err("Cancelado".into());
            }
            let want = ((total - seen) as usize).min(BLOCK);
            let n = read_block(dst.0, &mut buf[..want])?;
            if n == 0 {
                return Err("verificación: lectura corta".into());
            }
            check.update(&buf[..n]);
            seen += n as u64;
            add_verified(&state, slot, n as u64);
        }
        let got = check.finalize();
        if got != expect {
            return Err(format!(
                "CRC no coincide (origen {expect:#010x} destino {got:#010x}). El destino no es fiable."
            ));
        }
    }

    set_phase(&state, slot, DestPhase::Done, None);
    Ok(())
}

pub fn start_job(
    source: DiskInfo,
    dests: Vec<DiskInfo>,
    verify: bool,
) -> (Arc<JobState>, Vec<JoinHandle<()>>) {
    let progress = dests
        .iter()
        .map(|d| DestProgress {
            index: d.index,
            written: 0,
            verified: 0,
            total: source.size_bytes,
            bps: 0.0,
            phase: DestPhase::Idle,
            error: None,
        })
        .collect();
    let state = Arc::new(JobState {
        running: AtomicBool::new(true),
        cancel: AtomicBool::new(false),
        dests: Mutex::new(progress),
    });
    let done = Arc::new(AtomicU64::new(0));
    let n = dests.len() as u64;
    let mut handles = Vec::new();
    for (slot, dest) in dests.into_iter().enumerate() {
        let src = source.clone();
        let st = Arc::clone(&state);
        let counter = Arc::clone(&done);
        handles.push(thread::spawn(move || {
            if let Err(e) = copy_one(src, dest, Arc::clone(&st), slot, verify) {
                if st.dests.lock().unwrap()[slot].phase != DestPhase::Cancelled {
                    set_phase(&st, slot, DestPhase::Failed, Some(e));
                }
            }
            if counter.fetch_add(1, Ordering::Relaxed) + 1 >= n {
                st.running.store(false, Ordering::Relaxed);
            }
        }));
    }
    (state, handles)
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
