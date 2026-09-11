#![cfg(windows)]

use std::ffi::c_void;
use std::fs::File;
use std::io;
use std::mem::zeroed;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::ptr::{null, null_mut};
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

const GENERIC_WRITE: u32 = 0x4000_0000;
const FILE_SHARE_READ: u32 = 0x0000_0001;
const FILE_SHARE_WRITE: u32 = 0x0000_0002;
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

pub(crate) struct CancelableFile {
    handle: Handle,
    event: Handle,
    offset: u64,
}

// Both handles are uniquely owned and only moved with this value. The type intentionally is not Sync.
unsafe impl Send for CancelableFile {}

impl CancelableFile {
    pub(crate) fn reopen_at(path: &Path, offset: u64) -> io::Result<Self> {
        let wide = wide_path(path);
        let handle = unsafe {
            CreateFileW(
                wide.as_ptr(),
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
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

        Ok(Self { handle, event, offset })
    }

    pub(crate) fn write_all_cancelable(
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

impl Drop for CancelableFile {
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

pub(crate) fn sync_file_cancelable(
    file: File,
    mut cancelled: impl FnMut() -> bool,
) -> io::Result<()> {
    let (thread_tx, thread_rx) = mpsc::sync_channel(1);
    let (done_tx, done_rx) = mpsc::sync_channel(1);

    let helper = thread::spawn(move || {
        let thread_id = unsafe { GetCurrentThreadId() };
        let thread_handle = unsafe { OpenThread(THREAD_TERMINATE, 0, thread_id) };
        if thread_handle.is_null() {
            let err = io::Error::last_os_error();
            let _ = thread_tx.send(Err(err));
            return;
        }
        if thread_tx.send(Ok(thread_handle as usize)).is_err() {
            unsafe { CloseHandle(thread_handle) };
            return;
        }

        let result = file.sync_data();
        let _ = done_tx.send(result);
    });

    let thread_handle = match thread_rx.recv() {
        Ok(Ok(raw)) => raw as Handle,
        Ok(Err(e)) => {
            let _ = helper.join();
            return Err(e);
        }
        Err(_) => {
            let _ = helper.join();
            return Err(io::Error::other("el thread de sincronización terminó antes de iniciar"));
        }
    };

    let mut cancel_requested = false;
    let mut cancel_error = None;
    let result = loop {
        match done_rx.recv_timeout(Duration::from_millis(CANCEL_POLL_MS as u64)) {
            Ok(result) => break result,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if !cancel_requested && cancelled() {
                    cancel_requested = true;
                    if unsafe { CancelSynchronousIo(thread_handle) } == 0 {
                        let code = unsafe { GetLastError() };
                        if code != ERROR_NOT_FOUND {
                            cancel_error = Some(io::Error::from_raw_os_error(code as i32));
                        }
                    }
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                break Err(io::Error::other("el thread de sincronización terminó sin resultado"));
            }
        }
    };

    unsafe { CloseHandle(thread_handle) };
    let join_result = helper.join();
    if join_result.is_err() {
        return Err(io::Error::other("panic en el thread de sincronización"));
    }
    if let Some(err) = cancel_error {
        return Err(err);
    }
    if cancel_requested {
        return Err(cancelled_error());
    }
    result
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
