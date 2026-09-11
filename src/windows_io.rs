#![cfg(windows)]

use std::cell::RefCell;
use std::ffi::c_void;
use std::fs::File;
use std::io;
use std::mem::zeroed;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::ptr::{null, null_mut};
use std::rc::Rc;
use std::sync::mpsc;
use std::thread::{self, JoinHandle};
use std::time::Duration;

const GENERIC_WRITE: u32 = 0x4000_0000;
const FILE_SHARE_READ: u32 = 0x0000_0001;
const FILE_SHARE_WRITE: u32 = 0x0000_0002;
const FILE_SHARE_DELETE: u32 = 0x0000_0004;
const OPEN_EXISTING: u32 = 3;
const FILE_ATTRIBUTE_NORMAL: u32 = 0x0000_0080;
const FILE_FLAG_OVERLAPPED: u32 = 0x4000_0000;
const ERROR_IO_PENDING: u32 = 997;
const ERROR_NOT_FOUND: u32 = 1168;
const WAIT_OBJECT_0: u32 = 0;
const WAIT_TIMEOUT: u32 = 258;
const INFINITE: u32 = 0xffff_ffff;
const THREAD_TERMINATE: u32 = 0x0001;
const CANCEL_POLL_MS: u32 = 40;

type Handle = *mut c_void;
const INVALID_HANDLE_VALUE: Handle = -1isize as Handle;

#[repr(C)]
#[derive(Clone, Copy)]
struct OverlappedOffset {
    offset: u32,
    offset_high: u32,
}

#[repr(C)]
union OverlappedPosition {
    offset: OverlappedOffset,
    pointer: *mut c_void,
}

#[repr(C)]
struct Overlapped {
    internal: usize,
    internal_high: usize,
    position: OverlappedPosition,
    event: Handle,
}

#[link(name = "kernel32")]
extern "system" {
    fn CreateFileW(
        file_name: *const u16,
        desired_access: u32,
        share_mode: u32,
        security_attributes: *const c_void,
        creation_disposition: u32,
        flags_and_attributes: u32,
        template_file: Handle,
    ) -> Handle;
    fn CreateEventW(
        event_attributes: *const c_void,
        manual_reset: i32,
        initial_state: i32,
        name: *const u16,
    ) -> Handle;
    fn CloseHandle(object: Handle) -> i32;
    fn GetLastError() -> u32;
    fn ResetEvent(event: Handle) -> i32;
    fn WriteFile(
        file: Handle,
        buffer: *const c_void,
        bytes_to_write: u32,
        bytes_written: *mut u32,
        overlapped: *mut Overlapped,
    ) -> i32;
    fn WaitForSingleObject(handle: Handle, milliseconds: u32) -> u32;
    fn GetOverlappedResult(
        file: Handle,
        overlapped: *mut Overlapped,
        bytes_transferred: *mut u32,
        wait: i32,
    ) -> i32;
    fn CancelIoEx(file: Handle, overlapped: *const Overlapped) -> i32;
    fn GetCurrentThreadId() -> u32;
    fn OpenThread(desired_access: u32, inherit_handle: i32, thread_id: u32) -> Handle;
    fn CancelSynchronousIo(thread: Handle) -> i32;
}

struct NativeWriter {
    handle: Handle,
    event: Handle,
    offset: u64,
    path: PathBuf,
}

impl NativeWriter {
    fn open(path: &Path, offset: u64) -> io::Result<Self> {
        let wide = wide_path(path);
        let handle = unsafe {
            CreateFileW(
                wide.as_ptr(),
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                null(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
                null_mut(),
            )
        };
        if handle == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }

        let event = unsafe { CreateEventW(null(), 1, 0, null()) };
        if event.is_null() {
            let err = io::Error::last_os_error();
            unsafe { CloseHandle(handle) };
            return Err(err);
        }

        Ok(Self {
            handle,
            event,
            offset,
            path: path.to_path_buf(),
        })
    }

    fn matches(&self, path: &Path, offset: u64) -> bool {
        self.path == path && self.offset == offset
    }

    fn write_all_cancelable(
        &mut self,
        mut data: &[u8],
        mut cancelled: impl FnMut() -> bool,
    ) -> io::Result<()> {
        while !data.is_empty() {
            if cancelled() {
                return Err(cancelled_error());
            }

            let request_len = data.len().min(u32::MAX as usize) as u32;
            if unsafe { ResetEvent(self.event) } == 0 {
                return Err(io::Error::last_os_error());
            }

            let mut overlapped: Overlapped = unsafe { zeroed() };
            overlapped.position = OverlappedPosition {
                offset: OverlappedOffset {
                    offset: self.offset as u32,
                    offset_high: (self.offset >> 32) as u32,
                },
            };
            overlapped.event = self.event;

            let started = unsafe {
                WriteFile(
                    self.handle,
                    data.as_ptr().cast(),
                    request_len,
                    null_mut(),
                    &mut overlapped,
                )
            };

            if started == 0 {
                let code = unsafe { GetLastError() };
                if code != ERROR_IO_PENDING {
                    return Err(io::Error::from_raw_os_error(code as i32));
                }
            }

            loop {
                match unsafe { WaitForSingleObject(self.event, CANCEL_POLL_MS) } {
                    WAIT_OBJECT_0 => break,
                    WAIT_TIMEOUT => {
                        if cancelled() {
                            unsafe {
                                CancelIoEx(self.handle, &overlapped);
                                WaitForSingleObject(self.event, INFINITE);
                            }
                            return Err(cancelled_error());
                        }
                    }
                    _ => return Err(io::Error::last_os_error()),
                }
            }

            let mut transferred = 0u32;
            if unsafe { GetOverlappedResult(self.handle, &mut overlapped, &mut transferred, 0) } == 0 {
                return Err(io::Error::last_os_error());
            }
            if transferred == 0 {
                return Err(io::Error::new(
                    io::ErrorKind::WriteZero,
                    "WriteFile completó sin escribir bytes",
                ));
            }

            let done = transferred as usize;
            self.offset = self.offset.saturating_add(transferred as u64);
            data = &data[done..];
        }
        Ok(())
    }
}

impl Drop for NativeWriter {
    fn drop(&mut self) {
        unsafe {
            if !self.event.is_null() {
                CloseHandle(self.event);
            }
            if !self.handle.is_null() && self.handle != INVALID_HANDLE_VALUE {
                CloseHandle(self.handle);
            }
        }
    }
}

pub(crate) struct CancelableFile {
    inner: Rc<RefCell<NativeWriter>>,
}

impl CancelableFile {
    pub(crate) fn reopen_at(path: &Path, offset: u64) -> io::Result<Self> {
        WRITER_CACHE.with(|slot| {
            let mut slot = slot.borrow_mut();
            if let Some(existing) = slot.as_ref() {
                if existing.borrow().matches(path, offset) {
                    return Ok(Self {
                        inner: Rc::clone(existing),
                    });
                }
            }

            let writer = Rc::new(RefCell::new(NativeWriter::open(path, offset)?));
            *slot = Some(Rc::clone(&writer));
            Ok(Self { inner: writer })
        })
    }

    pub(crate) fn write_all_cancelable(
        &mut self,
        data: &[u8],
        cancelled: impl FnMut() -> bool,
    ) -> io::Result<()> {
        self.inner
            .borrow_mut()
            .write_all_cancelable(data, cancelled)
    }
}

enum SyncCommand {
    Sync(File),
    Stop,
}

struct SyncWorker {
    command_tx: mpsc::SyncSender<SyncCommand>,
    done_rx: mpsc::Receiver<io::Result<()>>,
    thread_handle: Handle,
    helper: Option<JoinHandle<()>>,
}

impl SyncWorker {
    fn new() -> io::Result<Self> {
        let (command_tx, command_rx) = mpsc::sync_channel(1);
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let (thread_tx, thread_rx) = mpsc::sync_channel(1);

        let helper = thread::spawn(move || {
            let thread_id = unsafe { GetCurrentThreadId() };
            let thread_handle = unsafe { OpenThread(THREAD_TERMINATE, 0, thread_id) };
            if thread_handle.is_null() {
                let _ = thread_tx.send(Err(io::Error::last_os_error()));
                return;
            }
            if thread_tx.send(Ok(thread_handle as usize)).is_err() {
                unsafe { CloseHandle(thread_handle) };
                return;
            }

            while let Ok(command) = command_rx.recv() {
                match command {
                    SyncCommand::Sync(file) => {
                        if done_tx.send(file.sync_data()).is_err() {
                            break;
                        }
                    }
                    SyncCommand::Stop => break,
                }
            }
        });

        let thread_handle = match thread_rx.recv() {
            Ok(Ok(raw)) => raw as Handle,
            Ok(Err(e)) => {
                let _ = helper.join();
                return Err(e);
            }
            Err(_) => {
                let _ = helper.join();
                return Err(io::Error::other("el worker de sincronización terminó antes de iniciar"));
            }
        };

        Ok(Self {
            command_tx,
            done_rx,
            thread_handle,
            helper: Some(helper),
        })
    }

    fn sync(
        &mut self,
        file: File,
        mut cancelled: impl FnMut() -> bool,
    ) -> io::Result<()> {
        self.command_tx
            .send(SyncCommand::Sync(file))
            .map_err(|_| io::Error::other("el worker de sincronización no está disponible"))?;

        let mut cancel_requested = false;
        let mut cancel_error = None;
        let result = loop {
            match self.done_rx.recv_timeout(Duration::from_millis(CANCEL_POLL_MS as u64)) {
                Ok(result) => break result,
                Err(mpsc::RecvTimeoutError::Timeout) => {
                    if cancelled() {
                        cancel_requested = true;
                        if unsafe { CancelSynchronousIo(self.thread_handle) } == 0 {
                            let code = unsafe { GetLastError() };
                            if code != ERROR_NOT_FOUND {
                                cancel_error = Some(io::Error::from_raw_os_error(code as i32));
                            }
                        }
                    }
                }
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    break Err(io::Error::other("el worker de sincronización terminó sin resultado"));
                }
            }
        };

        if let Some(err) = cancel_error {
            return Err(err);
        }
        if cancel_requested {
            return Err(cancelled_error());
        }
        result
    }
}

impl Drop for SyncWorker {
    fn drop(&mut self) {
        let _ = self.command_tx.send(SyncCommand::Stop);
        if let Some(helper) = self.helper.take() {
            let _ = helper.join();
        }
        unsafe {
            if !self.thread_handle.is_null() {
                CloseHandle(self.thread_handle);
            }
        }
    }
}

thread_local! {
    static WRITER_CACHE: RefCell<Option<Rc<RefCell<NativeWriter>>>> = const { RefCell::new(None) };
    static SYNC_WORKER: RefCell<Option<SyncWorker>> = const { RefCell::new(None) };
}

fn clear_writer_cache() {
    WRITER_CACHE.with(|slot| {
        slot.borrow_mut().take();
    });
}

pub(crate) fn sync_file_cancelable(
    file: File,
    cancelled: impl FnMut() -> bool,
) -> io::Result<()> {
    clear_writer_cache();
    SYNC_WORKER.with(|slot| {
        let mut slot = slot.borrow_mut();
        if slot.is_none() {
            *slot = Some(SyncWorker::new()?);
        }
        slot.as_mut()
            .expect("sync worker initialized")
            .sync(file, cancelled)
    })
}

fn cancelled_error() -> io::Error {
    io::Error::new(io::ErrorKind::Interrupted, "cancelado")
}

fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_file(name: &str) -> std::path::PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("disk-duplicator-win-io-{name}-{stamp}.bin"))
    }

    #[test]
    fn overlapped_writer_coexists_with_std_file_handle() {
        clear_writer_cache();
        let path = temp_file("overlapped");
        let owner = File::create(&path).unwrap();
        let mut writer = CancelableFile::reopen_at(&path, 0).unwrap();
        writer.write_all_cancelable(b"abcdef", || false).unwrap();
        drop(writer);
        clear_writer_cache();
        owner.sync_data().unwrap();
        drop(owner);
        assert_eq!(fs::read(&path).unwrap(), b"abcdef");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn overlapped_writer_honors_pre_cancel() {
        clear_writer_cache();
        let path = temp_file("cancel");
        let _owner = File::create(&path).unwrap();
        let mut writer = CancelableFile::reopen_at(&path, 0).unwrap();
        let err = writer.write_all_cancelable(b"data", || true).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::Interrupted);
        clear_writer_cache();
        let _ = fs::remove_file(path);
    }

    #[test]
    fn overlapped_writer_is_reused_at_expected_offset() {
        clear_writer_cache();
        let path = temp_file("reuse");
        let owner = File::create(&path).unwrap();

        let mut first = CancelableFile::reopen_at(&path, 0).unwrap();
        first.write_all_cancelable(b"abc", || false).unwrap();
        let mut second = CancelableFile::reopen_at(&path, 3).unwrap();
        assert!(Rc::ptr_eq(&first.inner, &second.inner));
        second.write_all_cancelable(b"def", || false).unwrap();

        drop(second);
        drop(first);
        clear_writer_cache();
        owner.sync_data().unwrap();
        drop(owner);
        assert_eq!(fs::read(&path).unwrap(), b"abcdef");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn thread_local_sync_worker_handles_multiple_files() {
        clear_writer_cache();
        let path_a = temp_file("sync-a");
        let path_b = temp_file("sync-b");
        let mut a = File::create(&path_a).unwrap();
        let mut b = File::create(&path_b).unwrap();
        a.write_all(b"alpha").unwrap();
        b.write_all(b"beta").unwrap();

        sync_file_cancelable(a, || false).unwrap();
        sync_file_cancelable(b, || false).unwrap();

        assert_eq!(fs::read(&path_a).unwrap(), b"alpha");
        assert_eq!(fs::read(&path_b).unwrap(), b"beta");
        let _ = fs::remove_file(path_a);
        let _ = fs::remove_file(path_b);
    }
}
