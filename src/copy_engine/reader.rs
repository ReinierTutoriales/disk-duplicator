use windows::Win32::Storage::FileSystem::*;
use windows::Win32::Foundation::*;
use windows::core::*;
use std::sync::{Arc, Mutex};
use crossbeam_channel::Sender;
use super::types::{BufferChunk, CopyProgress, DestState};
use super::buffer::AlignedBuffer;
use crate::disk_enum::types::DiskInfo;

pub fn reader_thread(
    source: DiskInfo,
    buffer_size: usize,
    senders: Vec<Sender<Option<BufferChunk>>>,
    progress: Arc<Mutex<CopyProgress>>,
    verify: bool,
) -> Result<(), String> {
    let path_wide: Vec<u16> = source.physical_path.to_string_lossy()
        .encode_utf16().chain(std::iter::once(0)).collect();
    
    let handle = unsafe {
        CreateFileW(
            PCWSTR(path_wide.as_ptr()),
            GENERIC_READ.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN,
            None,
        ).map_err(|e| format!("Failed to open source: {}", e))?
    };
    
    let mut offset = 0u64;
    let mut hasher = if verify { Some(blake3::Hasher::new()) } else { None };
    
    loop {
        let mut buffer = AlignedBuffer::new(buffer_size, 4096);
        
        let mut bytes_read = 0u32;
        let result = unsafe {
            let mut overlapped = OVERLAPPED {
                Internal: 0,
                InternalHigh: 0,
                Anonymous: OVERLAPPED_0 {
                    Anonymous: OVERLAPPED_0_0 {
                        Offset: offset as u32,
                        OffsetHigh: (offset >> 32) as u32,
                    },
                },
                hEvent: HANDLE::default(),
            };
            
            ReadFile(
                handle,
                Some(buffer.as_mut_slice()),
                Some(&mut bytes_read),
                Some(&mut overlapped),
            )
        };
        
        if result.is_err() {
            let err = std::io::Error::last_os_error();
            if err.raw_os_error() == Some(ERROR_HANDLE_EOF.0 as i32) {
                break;
            }
            return Err(format!("Read error at offset {}: {}", offset, err));
        }
        
        if bytes_read == 0 {
            break;
        }
        
        let chunk_hash = if let Some(ref mut h) = hasher {
            h.update(&buffer.as_slice()[..bytes_read as usize]);
            Some(h.finalize().into())
        } else {
            None
        };
        
        let chunk = BufferChunk {
            offset,
            data: buffer.as_slice()[..bytes_read as usize].to_vec(),
            hash: chunk_hash,
        };
        
        for sender in &senders {
            if sender.send(Some(chunk.clone())).is_err() {
                return Err("Writer channel closed".to_string());
            }
        }
        
        {
            let mut prog = progress.lock().unwrap();
            prog.bytes_copied += bytes_read as u64;
        }
        
        offset += bytes_read as u64;
    }
    
    for sender in senders {
        let _ = sender.send(None);
    }
    
    unsafe { CloseHandle(handle).map_err(|e| format!("Failed to close handle: {}", e))?; }
    
    Ok(())
}
