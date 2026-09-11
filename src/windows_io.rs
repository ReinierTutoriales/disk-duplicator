#![cfg(windows)]

use std::ffi::OsStr;
use std::io;
use std::mem::zeroed;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::ptr::{null, null_mut};

use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_IO_PENDING, HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0,
    WAIT_TIMEOUT,
};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FlushFileBuffers, SetEndOfFile, SetFilePointerEx, WriteFile, CREATE_ALWAYS,
    FILE_ATTRIBUTE_NORMAL, FILE_BEGIN, FILE_FLAG_OVERLAPPED, FILE_SHARE_READ, FILE_SHARE_WRITE,
    GENERIC_WRITE, OPEN_EXISTING,
};
use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
use windows_sys::Win32::System::Threading::{CreateEventW, ResetEvent, WaitForSingleObject};

const CANCEL_POLL_MS: u32 = 40;

pub(crate) struct CancelableFile {
    handle: HANDLE,
    event: HANDLE,
    offset: u64,
}

// Ownership of both Win32 handles is unique to this value. The type is deliberately not Sync.
unsafe impl Send for CancelableFile {}

impl CancelableFile {
    pub(crate) fn create(path: &Path) -> io::Result<Self> {
        Self::open(path, CREATE_ALWAYS, 0)
    }

    pub(crate) fn reopen_at(path: &Path, offset: u64) -> io::Result<Self> {
        Self::open(path, OPEN_EXISTING, offset)
    }

    fn open(path: &Path, disposition: u32, offset: u64) -> io::Result<Self> {
        let wide = wide_path(path);
        let handle = unsafe {
            CreateFileW(
                wide.as_ptr(),
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                null(),
                disposition,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
                0,
            )
        };
        if handle == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }

        let event = unsafe { CreateEventW(null(), 1, 0, null()) };
        if event == 0 {
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
            unsafe { ResetEvent(self.event) };

            let mut overlapped: OVERLAPPED = unsafe { zeroed() };
            overlapped.Anonymous.Anonymous.Offset = self.offset as u32;
            overlapped.Anonymous.Anonymous.OffsetHigh = (self.offset >> 32) as u32;
            overlapped.hEvent = self.event;

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
                                WaitForSingleObject(self.event, u32::MAX);
                            }
                            return Err(io::Error::new(io::ErrorKind::Interrupted, "cancelado"));
                        }
                    }
                    _ => return Err(io::Error::last_os_error()),
                }
            }

            let mut transferred = 0u32;
            if unsafe { GetOverlappedResult(self.handle, &overlapped, &mut transferred, 0) } == 0 {
                return Err(io::Error::last_os_error());
            }
            if transferred == 0 {
                return Err(io::Error::new(io::ErrorKind::WriteZero, "WriteFile completó sin escribir bytes"));
            }

            let done = transferred as usize;
            self.offset = self.offset.saturating_add(transferred as u64);
            data = &data[done..];
        }
        Ok(())
    }

    pub(crate) fn truncate_to(&mut self, len: u64) -> io::Result<()> {
        let distance = len as i64;
        if unsafe { SetFilePointerEx(self.handle, distance, null_mut(), FILE_BEGIN) } == 0 {
            return Err(io::Error::last_os_error());
        }
        if unsafe { SetEndOfFile(self.handle) } == 0 {
            return Err(io::Error::last_os_error());
        }
        self.offset = len;
        Ok(())
    }

    pub(crate) fn sync_data(&self) -> io::Result<()> {
        if unsafe { FlushFileBuffers(self.handle) } == 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(())
        }
    }
}

impl Drop for CancelableFile {
    fn drop(&mut self) {
        unsafe {
            if self.event != 0 {
                CloseHandle(self.event);
            }
            if self.handle != 0 && self.handle != INVALID_HANDLE_VALUE {
                CloseHandle(self.handle);
            }
        }
    }
}

fn wide_path(path: &Path) -> Vec<u16> {
    OsStr::new(path.as_os_str())
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}
