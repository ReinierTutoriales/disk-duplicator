use crate::disks::{letters_for_disk, open_disk, DiskInfo, RawHandle};
use crate::winpath::{pcw, volume_path, wide_z};
use std::io::{Read, Seek, SeekFrom, Write};
use std::os::windows::io::{FromRawHandle, RawHandle as StdRaw};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Instant;
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows::Win32::System::Ioctl::{FSCTL_DISMOUNT_VOLUME, FSCTL_LOCK_VOLUME};
use windows::Win32::System::IO::DeviceIoControl;

const BLOCK: usize = 8 * 1024 * 1024;
const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;
const FSCTL_ALLOW_EXTENDED_DASD_IO: u32 = 0x0009_0083;

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum DestPhase {
    Idle,
    Locking,
    Copying,
    Done,
    Failed,
    Cancelled,
}

#[derive(Clone)]
pub struct DestProgress {
    pub index: u32,
    pub written: u64,
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

fn file_from_handle(h: RawHandle) -> std::fs::File {
    let raw = h.0 .0 as StdRaw;
    std::mem::forget(h);
    unsafe { std::fs::File::from_raw_handle(raw) }
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
    .map_err(|e| format!("{letter}: {e}"))
}

fn ctl(h: &RawHandle, code: u32) -> bool {
    let mut ret = 0u32;
    unsafe { DeviceIoControl(h.0, code, None, 0, None, 0, Some(&mut ret), None) }.is_ok()
}

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
        let _ = ctl(&h, FSCTL_ALLOW_EXTENDED_DASD_IO);
        let locked = ctl(&h, FSCTL_LOCK_VOLUME);
        if !locked {
            let _ = ctl(&h, FSCTL_DISMOUNT_VOLUME);
            let _ = ctl(&h, FSCTL_LOCK_VOLUME);
        } else {
            let _ = ctl(&h, FSCTL_DISMOUNT_VOLUME);
        }
        held.push(h);
    }
    Ok(held)
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

fn copy_one(source: DiskInfo, dest: DiskInfo, state: Arc<JobState>, slot: usize) -> Result<(), String> {
    set_phase(&state, slot, DestPhase::Locking, None);
    let _volume_locks = lock_volumes(dest.index, &dest.letters)?;
    let src_h = open_disk(&source.path, false).map_err(|e| format!("origen win32 {e}"))?;
    let dst_h = open_disk(&dest.path, true).map_err(|e| format!("destino win32 {e}"))?;
    let mut src = file_from_handle(src_h);
    let mut dst = file_from_handle(dst_h);
    src.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
    dst.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
    set_phase(&state, slot, DestPhase::Copying, None);
    let mut buf = vec![0u8; BLOCK];
    let mut copied = 0u64;
    let total = source.size_bytes;
    let t0 = Instant::now();
    while copied < total {
        if state.cancel.load(Ordering::Relaxed) {
            set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
            return Err("Cancelado".into());
        }
        let want = ((total - copied) as usize).min(BLOCK);
        let n = src.read(&mut buf[..want]).map_err(|e| format!("lectura: {e}"))?;
        if n == 0 {
            break;
        }
        dst.write_all(&buf[..n]).map_err(|e| format!("escritura: {e}"))?;
        copied += n as u64;
        add_written(&state, slot, n as u64, t0.elapsed().as_secs_f64());
    }
    dst.flush().map_err(|e| e.to_string())?;
    set_phase(&state, slot, DestPhase::Done, None);
    Ok(())
}

pub fn start_job(source: DiskInfo, dests: Vec<DiskInfo>) -> (Arc<JobState>, Vec<JoinHandle<()>>) {
    let progress = dests
        .iter()
        .map(|d| DestProgress {
            index: d.index,
            written: 0,
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
            if let Err(e) = copy_one(src, dest, Arc::clone(&st), slot) {
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
