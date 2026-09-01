pub mod types;
pub mod buffer;
pub mod reader;
pub mod writer;

use crossbeam_channel::{bounded, Sender, Receiver};
use std::sync::{Arc, Mutex};
use std::thread;
use types::{CopyJob, CopyProgress, DestState, BufferChunk};

pub struct CopyEngine {
    job: CopyJob,
    progress: Arc<Mutex<CopyProgress>>,
}

impl CopyEngine {
    pub fn new(job: CopyJob) -> Self {
        let total_bytes = job.source.size_bytes;
        let dest_status = job.destinations.iter().map(|d| types::DestStatus {
            disk_index: d.index,
            bytes_written: 0,
            status: DestState::Pending,
            error: None,
        }).collect();
        
        Self {
            job,
            progress: Arc::new(Mutex::new(CopyProgress {
                bytes_copied: 0,
                total_bytes,
                destination_status: dest_status,
            })),
        }
    }
    
    pub fn execute(&mut self) -> Result<(), String> {
        for dest in &self.job.destinations {
            if dest.size_bytes < self.job.source.size_bytes {
                return Err(format!(
                    "Destination disk {} ({} bytes) is smaller than source ({} bytes)",
                    dest.index, dest.size_bytes, self.job.source.size_bytes
                ));
            }
        }
        
        let channel_capacity = self.job.destinations.len() * 2;
        let mut senders: Vec<Sender<Option<BufferChunk>>> = Vec::new();
        let mut receivers: Vec<Receiver<Option<BufferChunk>>> = Vec::new();
        
        for _ in &self.job.destinations {
            let (tx, rx) = bounded(channel_capacity);
            senders.push(tx);
            receivers.push(rx);
        }
        
        let mut writer_handles = Vec::new();
        for (i, (dest, rx)) in self.job.destinations.iter().zip(receivers.into_iter()).enumerate() {
            let progress = Arc::clone(&self.progress);
            let verify = self.job.verify;
            let handle = thread::spawn(move || {
                writer::writer_thread(dest.clone(), rx, progress, i, verify)
            });
            writer_handles.push(handle);
        }
        
        let reader_handle = {
            let source = self.job.source.clone();
            let buffer_size = self.job.buffer_size;
            let senders = senders;
            let progress = Arc::clone(&self.progress);
            let verify = self.job.verify;
            
            thread::spawn(move || {
                reader::reader_thread(source, buffer_size, senders, progress, verify)
            })
        };
        
        if let Err(e) = reader_handle.join().map_err(|_| "Reader thread panicked")? {
            return Err(format!("Reader failed: {}", e));
        }
        
        for handle in writer_handles {
            if let Err(e) = handle.join().map_err(|_| "Writer thread panicked")? {
                eprintln!("Writer failed: {}", e);
            }
        }
        
        let progress = self.progress.lock().unwrap();
        let failed_count = progress.destination_status.iter()
            .filter(|s| matches!(s.status, DestState::Failed))
            .count();
        
        if failed_count > 0 {
            return Err(format!("{} destination(s) failed", failed_count));
        }
        
        Ok(())
    }
    
    pub fn get_progress(&self) -> CopyProgress {
        self.progress.lock().unwrap().clone()
    }
}
