use crate::disk_enum::types::DiskInfo;

pub struct CopyJob {
    pub source: DiskInfo,
    pub destinations: Vec<DiskInfo>,
    pub buffer_size: usize,
    pub verify: bool,
}

#[derive(Clone)]
pub struct CopyProgress {
    pub bytes_copied: u64,
    pub total_bytes: u64,
    pub destination_status: Vec<DestStatus>,
}

#[derive(Clone)]
pub struct DestStatus {
    pub disk_index: u32,
    pub bytes_written: u64,
    pub status: DestState,
    pub error: Option<String>,
}

#[derive(Clone, PartialEq)]
pub enum DestState {
    Pending,
    Copying,
    Completed,
    Failed,
}

#[derive(Clone)]
pub struct BufferChunk {
    pub offset: u64,
    pub data: Vec<u8>,
    pub hash: Option<[u8; 32]>,
}
