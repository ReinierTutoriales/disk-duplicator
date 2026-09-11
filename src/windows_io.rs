#![cfg(windows)]

use std::ffi::c_void;
use std::io;
use std::mem::zeroed;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::ptr::{null, null_mut};

const GENERIC_WRITE: u32 = 0x4000_0000;
const FILE_SHARE_READ: u32 = 0x0000_0001;
const FILE_SHARE_WRITE: u32 = 0x0000_0002;
const OPEN_EXISTING: u32 = 3;
const FILE_ATTRIBUTE_NORMAL: u32 = 0x0000_0080;
const FILE_FLAG_OVERLAPPED: u32 = 0x4000_0000;
const ERROR_IO_PENDING: u32 = 997;
const WAIT_OBJECT_0: u32 = 0;
const WAIT_TIMEOUT: u32 = 258;
const INFINITE: u32 = 0xffff_ffff;
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
                return Err(io::Error::new(io::ErrorKind::Interrupted, "cancelado"));
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
                            return Err(io::Error::new(io::ErrorKind::Interrupted, "cancelado"));
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

fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}
