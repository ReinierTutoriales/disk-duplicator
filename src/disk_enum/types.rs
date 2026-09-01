use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct DiskInfo {
    pub physical_path: PathBuf,
    pub index: u32,
    pub model: String,
    pub serial: String,
    pub size_bytes: u64,
    pub sector_size: u32,
    pub bus_type: BusType,
    pub is_removable: bool,
    pub is_system_disk: bool,
    pub volume_letters: Vec<char>,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum BusType {
    Unknown,
    Usb,
    Sata,
    Nvme,
    Other(u32),
}
