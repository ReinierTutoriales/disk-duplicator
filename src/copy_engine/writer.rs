use windows::Win32::Storage::FileSystem::*;
use windows::Win32::Foundation::*;
use windows::core::*;
use std::sync::{Arc, Mutex};
use crossbeam_channel::Receiver;
use super::types::{BufferChunk, CopyProgress, DestState};
use super::buffer::AlignedBuffer;
use crate::disk_enum::types::DiskInfo;

pub fn writer_thread(
    dest: DiskInfo,
    receiver: Receiver<Option<BufferChunk>>,
    progress: Arc<Mutex<CopyProgress>>,
    dest_index: usize,
    verify: bool,
) -> Result<(), String> {
    let path_wide: Vec<u16> = dest.physical_path.to_string_lossy()
        .encode_utf16().chain(std::iter::once(0)).collect();
    
    let handle = unsafe {
        CreateFileW(
            PCWSTR(path_wide.as_ptr()),
            GENERIC_WRITE.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
            None,
        ).map_err(|e| format!("Failed to open destination: {}", e))?
    };
    
    let mut hasher = if verify { Some(blake3::Hasher::new()) } else { None };
    
    {
        let mut prog = progress.lock().unwrap();
        prog.destination_status[dest_index].status = DestState::Copying;
    }
    
    while let Ok(chunk_opt) = receiver.recv() {
        let chunk = match chunk_opt {
            Some(c) => c,
            None => break,
        };
        
        let mut buffer = AlignedBuffer::new(chunk.data.len().max(4096), 4096);
        buffer.as_mut_slice()[..chunk.data.len()].copy_from_slice(&chunk.data);
        
        let mut bytes_written = 0u32;
        let result = unsafe {
            let mut overlapped = OVERLAPPED {
                Internal: 0,
                InternalHigh: 0,
                Anonymous: OVERLAPPED_0 {
                    Anonymous: OVERLAPPED_0_0 {
                        Offset: chunk.offset as u32,
                        OffsetHigh: (chunk.offset >> 32) as u32,
                    },
                },
                hEvent: HANDLE::default(),
            };
            
            WriteFile(
                handle,
                Some(buffer.as_slice()),
                Some(&mut bytes_written),
                Some(&mut overlapped),
            )
        };
        
        if result.is_err() {
            let err = std::io::Error::last_os_error();
            
            {
                let mut prog = progress.lock().unwrap();
                prog.destination_status[dest_index].status = DestState::Failed;
                prog.destination_status[dest_index].error = Some(format!("{}", err));
            }
            
            unsafe { let _ = CloseHandle(handle); }
            return Err(format!("Write error at offset {}: {}", chunk.offset, err));
        }
        
        if let Some(ref mut h) = hasher {
            h.update(&chunk.data);
        }
        
        {
            let mut prog = progress.lock().unwrap();
            prog.destination_status[dest_index].bytes_written += bytes_written as u64;
        }
    }
    
    {
        let mut prog = progress.lock().unwrap();
        prog.destination_status[dest_index].status = DestState::Completed;
    }
    
    unsafe { CloseHandle(handle).map_err(|e| format!("Failed to close handle: {}", e))?; }
    
    Ok(())
}
