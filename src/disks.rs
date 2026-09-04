use crate::winpath::{pcw, physical_path, volume_path, wide_z};
use std::mem::size_of;
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, GetDriveTypeW, GetLogicalDrives, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ,
    FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows::Win32::System::Ioctl::{
    GET_LENGTH_INFORMATION, IOCTL_DISK_GET_LENGTH_INFO, IOCTL_STORAGE_QUERY_PROPERTY,
    STORAGE_DEVICE_DESCRIPTOR, STORAGE_PROPERTY_QUERY, PropertyStandardQuery, StorageDeviceProperty,
};
use windows::Win32::System::IO::DeviceIoControl;
use windows::Win32::System::SystemInformation::GetWindowsDirectoryW;

const IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS: u32 = 0x0056_0000;
const IOCTL_DISK_GET_DRIVE_GEOMETRY_EX: u32 = 0x0007_00A0;
const IOCTL_STORAGE_GET_DEVICE_NUMBER: u32 = 0x002D_1080;
const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;

pub const FILE_FLAG_NO_BUFFERING: u32 = 0x2000_0000;
pub const FILE_FLAG_WRITE_THROUGH: u32 = 0x8000_0000;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BusKind {
    Unknown,
    Usb,
    Sata,
    Nvme,
    Ata,
    Virtual,
    Other,
}

impl BusKind {
    pub fn label(self) -> &'static str {
        match self {
            BusKind::Usb => "USB",
            BusKind::Sata => "SATA",
            BusKind::Nvme => "NVMe",
            BusKind::Ata => "ATA",
            BusKind::Virtual => "Virtual",
            BusKind::Other => "Otro",
            BusKind::Unknown => "?",
        }
    }

    fn from_raw(v: i32) -> Self {
        match v {
            0 => BusKind::Unknown,
            1 | 2 | 3 => BusKind::Ata,
            7 | 12 | 13 => BusKind::Usb,
            8 | 10 | 11 => BusKind::Sata,
            17 => BusKind::Nvme,
            14 | 15 => BusKind::Virtual,
            _ => BusKind::Other,
        }
    }
}

#[derive(Debug, Clone)]
pub struct DiskInfo {
    pub index: u32,
    pub path: String,
    pub model: String,
    pub serial: String,
    pub size_bytes: u64,
    pub bus: BusKind,
    pub removable: bool,
    pub letters: Vec<char>,
    pub is_system: bool,
}

impl DiskInfo {
    pub fn size_gb(&self) -> f64 {
        self.size_bytes as f64 / 1_073_741_824.0
    }

    pub fn label(&self) -> String {
        let vols = if self.letters.is_empty() {
            String::new()
        } else {
            format!(
                " [{}]",
                self.letters
                    .iter()
                    .map(|c| format!("{c}:"))
                    .collect::<Vec<_>>()
                    .join(" ")
            )
        };
        format!(
            "PD{} {}  {:.1} GB  {}{}",
            self.index,
            self.model,
            self.size_gb(),
            self.bus.label(),
            vols
        )
    }
}

pub struct RawHandle(pub HANDLE);

impl Drop for RawHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
}

fn last_err() -> u32 {
    windows::core::Error::from_win32().code().0 as u32
}

fn create_file(path: &str, access: u32, flags: u32) -> Result<RawHandle, u32> {
    let w = wide_z(path);
    match unsafe {
        CreateFileW(
            pcw(&w),
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            windows::Win32::Storage::FileSystem::FILE_FLAGS_AND_ATTRIBUTES(flags),
            None,
        )
    } {
        Ok(h) if !h.is_invalid() => Ok(RawHandle(h)),
        Ok(_) => Err(last_err()),
        Err(e) => Err(e.code().0 as u32),
    }
}

/// Enumeración: puede relajar acceso. Nunca usar esto para escribir.
pub fn open_disk(path: &str, write: bool) -> Result<RawHandle, u32> {
    let acc = if write {
        GENERIC_READ | GENERIC_WRITE
    } else {
        GENERIC_READ
    };
    create_file(path, acc, FILE_ATTRIBUTE_NORMAL.0)
        .or_else(|_| create_file(path, if write { GENERIC_WRITE } else { GENERIC_READ }, FILE_ATTRIBUTE_NORMAL.0))
        .or_else(|_| {
            if write {
                Err(5)
            } else {
                create_file(path, 0, FILE_ATTRIBUTE_NORMAL.0)
            }
        })
}

/// Copia: permiso exacto o error. Sin fallback a handle de solo lectura.
pub fn open_disk_strict(path: &str, write: bool, extra_flags: u32) -> Result<RawHandle, String> {
    let acc = if write {
        GENERIC_READ | GENERIC_WRITE
    } else {
        GENERIC_READ
    };
    create_file(path, acc, FILE_ATTRIBUTE_NORMAL.0 | extra_flags).map_err(|e| {
        if e == 5 {
            format!("{path}: acceso denegado (5). Admin + volumen desmontado.")
        } else {
            format!("{path}: CreateFile Win32 {e}")
        }
    })
}

pub fn ioctl(
    h: HANDLE,
    code: u32,
    inp: Option<*const u8>,
    in_len: u32,
    out: Option<*mut u8>,
    out_len: u32,
) -> bool {
    let mut ret = 0u32;
    unsafe {
        DeviceIoControl(
            h,
            code,
            inp.map(|p| p as *const _),
            in_len,
            out.map(|p| p as *mut _),
            out_len,
            Some(&mut ret),
            None,
        )
        .is_ok()
    }
}

fn disk_length(h: HANDLE) -> Option<u64> {
    let mut info = GET_LENGTH_INFORMATION::default();
    if ioctl(
        h,
        IOCTL_DISK_GET_LENGTH_INFO,
        None,
        0,
        Some((&mut info as *mut GET_LENGTH_INFORMATION).cast()),
        size_of::<GET_LENGTH_INFORMATION>() as u32,
    ) && info.Length > 0
    {
        return Some(info.Length as u64);
    }
    let mut geo = [0u8; 64];
    if ioctl(
        h,
        IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
        None,
        0,
        Some(geo.as_mut_ptr()),
        geo.len() as u32,
    ) {
        let size = i64::from_le_bytes(geo[24..32].try_into().ok()?);
        if size > 0 {
            return Some(size as u64);
        }
    }
    None
}

fn descriptor(h: HANDLE) -> (String, String, BusKind, bool) {
    let query = STORAGE_PROPERTY_QUERY {
        PropertyId: StorageDeviceProperty,
        QueryType: PropertyStandardQuery,
        AdditionalParameters: [0; 1],
    };
    let mut buf = vec![0u8; 2048];
    if !ioctl(
        h,
        IOCTL_STORAGE_QUERY_PROPERTY,
        Some((&query as *const STORAGE_PROPERTY_QUERY).cast()),
        size_of::<STORAGE_PROPERTY_QUERY>() as u32,
        Some(buf.as_mut_ptr()),
        buf.len() as u32,
    ) {
        return ("Desconocido".into(), "-".into(), BusKind::Unknown, false);
    }
    let model = join_id(&buf, u32_at(&buf, 12), u32_at(&buf, 16));
    let serial = cstr_at(&buf, u32_at(&buf, 24)).unwrap_or_else(|| "-".into());
    (
        if model.is_empty() {
            "Desconocido".into()
        } else {
            model
        },
        serial,
        BusKind::from_raw(i32_at(&buf, 28)),
        buf.get(10).copied().unwrap_or(0) != 0,
    )
}

fn u32_at(buf: &[u8], off: usize) -> u32 {
    if off + 4 > buf.len() {
        return 0;
    }
    u32::from_le_bytes(buf[off..off + 4].try_into().unwrap())
}

fn i32_at(buf: &[u8], off: usize) -> i32 {
    if off + 4 > buf.len() {
        return 0;
    }
    i32::from_le_bytes(buf[off..off + 4].try_into().unwrap())
}

fn cstr_at(buf: &[u8], off: u32) -> Option<String> {
    if off == 0 || off as usize >= buf.len() {
        return None;
    }
    let t = buf[off as usize..]
        .iter()
        .copied()
        .take_while(|&b| b != 0)
        .map(|b| b as char)
        .collect::<String>()
        .trim()
        .to_string();
    if t.is_empty() {
        None
    } else {
        Some(t)
    }
}

fn join_id(buf: &[u8], vendor: u32, product: u32) -> String {
    [cstr_at(buf, vendor), cstr_at(buf, product)]
        .into_iter()
        .flatten()
        .collect::<Vec<_>>()
        .join(" ")
}

fn windows_dir_letter() -> Option<char> {
    let mut buf = [0u16; 260];
    let n = unsafe { GetWindowsDirectoryW(Some(&mut buf)) };
    if n == 0 {
        return None;
    }
    String::from_utf16_lossy(&buf[..n as usize]).chars().next()
}

fn volume_device_number(letter: char) -> Option<u32> {
    let h = create_file(&volume_path(letter), GENERIC_READ, FILE_ATTRIBUTE_NORMAL.0)
        .or_else(|_| create_file(&volume_path(letter), 0, FILE_ATTRIBUTE_NORMAL.0))
        .ok()?;
    let mut buf = [0u8; 12];
    if ioctl(
        h.0,
        IOCTL_STORAGE_GET_DEVICE_NUMBER,
        None,
        0,
        Some(buf.as_mut_ptr()),
        buf.len() as u32,
    ) {
        return Some(u32_at(&buf, 4));
    }
    let mut ext = vec![0u8; 256];
    if ioctl(
        h.0,
        IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
        None,
        0,
        Some(ext.as_mut_ptr()),
        ext.len() as u32,
    ) && u32_at(&ext, 0) >= 1
    {
        return Some(u32_at(&ext, 4));
    }
    None
}

pub fn letters_for_disk(index: u32) -> Vec<char> {
    let mut out = Vec::new();
    let mask = unsafe { GetLogicalDrives() };
    for i in 0..26u32 {
        if mask & (1 << i) == 0 {
            continue;
        }
        let letter = char::from(b'A' + i as u8);
        let root = wide_z(&format!(r"{letter}:\\"));
        if unsafe { GetDriveTypeW(pcw(&root)) } == 5 {
            continue;
        }
        if volume_device_number(letter) == Some(index) {
            out.push(letter);
        }
    }
    out
}

pub fn enumerate_disks() -> Result<Vec<DiskInfo>, String> {
    let mut disks = Vec::new();
    let mut first_err: Option<(u32, u32)> = None;
    for i in 0..32u32 {
        let path = physical_path(i);
        match open_disk(&path, false) {
            Ok(h) => {
                let Some(size) = disk_length(h.0) else {
                    if first_err.is_none() {
                        first_err = Some((i, last_err()));
                    }
                    continue;
                };
                if size == 0 {
                    continue;
                }
                let (model, serial, bus, removable) = descriptor(h.0);
                disks.push(DiskInfo {
                    index: i,
                    path,
                    model,
                    serial,
                    size_bytes: size,
                    bus,
                    removable,
                    letters: Vec::new(),
                    is_system: false,
                });
            }
            Err(e) => {
                if i == 0 {
                    first_err = Some((i, e));
                }
            }
        }
    }
    if disks.is_empty() {
        return Err(match first_err {
            Some((i, 5)) => format!("PD{i} acceso denegado (5). Ejecuta como administrador."),
            Some((i, e)) => format!("PD{i} error Win32 {e}."),
            None => "No se abrió ningún PhysicalDrive.".into(),
        });
    }
    for d in disks.iter_mut() {
        d.letters = letters_for_disk(d.index);
    }
    if let Some(sys) = windows_dir_letter() {
        for d in disks.iter_mut() {
            d.is_system = d.letters.contains(&sys);
        }
    }
    disks.sort_by_key(|d| d.index);
    Ok(disks)
}
