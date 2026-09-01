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
const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;

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
    pub fn size_gb(&self) -> u64 {
        self.size_bytes / 1_073_741_824
    }
}

struct Handle(HANDLE);
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

fn last_err() -> u32 {
    windows::core::Error::from_win32().code().0 as u32
}

fn open_path(path: &str, access: u32) -> Result<Handle, u32> {
    let w = wide_z(path);
    match unsafe {
        CreateFileW(
            pcw(&w),
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            None,
        )
    } {
        Ok(h) if !h.is_invalid() => Ok(Handle(h)),
        Ok(_) => Err(last_err()),
        Err(e) => Err(e.code().0 as u32),
    }
}

fn open_query(path: &str) -> Result<Handle, u32> {
    open_path(path, GENERIC_READ)
        .or_else(|_| open_path(path, GENERIC_READ | GENERIC_WRITE))
        .or_else(|_| open_path(path, 0))
}

fn ioctl(h: HANDLE, code: u32, inp: Option<*const u8>, in_len: u32, out: *mut u8, out_len: u32) -> bool {
    let mut ret = 0u32;
    unsafe {
        DeviceIoControl(
            h,
            code,
            inp.map(|p| p as *const _),
            in_len,
            Some(out.cast()),
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
        (&mut info as *mut GET_LENGTH_INFORMATION).cast(),
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
        geo.as_mut_ptr(),
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
    let ok = ioctl(
        h,
        IOCTL_STORAGE_QUERY_PROPERTY,
        Some((&query as *const STORAGE_PROPERTY_QUERY).cast()),
        size_of::<STORAGE_PROPERTY_QUERY>() as u32,
        buf.as_mut_ptr(),
        buf.len() as u32,
    );
    if !ok {
        return ("Desconocido".into(), "-".into(), BusKind::Unknown, false);
    }
    let vendor_off = u32_at(&buf, 12);
    let product_off = u32_at(&buf, 16);
    let serial_off = u32_at(&buf, 24);
    let bus_raw = i32_at(&buf, 28);
    let removable = buf.get(10).copied().unwrap_or(0) != 0;
    let model = join_id(&buf, vendor_off, product_off);
    let serial = cstr_at(&buf, serial_off).unwrap_or_else(|| "-".into());
    (
        if model.is_empty() {
            "Desconocido".into()
        } else {
            model
        },
        serial,
        BusKind::from_raw(bus_raw),
        removable,
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
    let s = buf[off as usize..]
        .iter()
        .copied()
        .take_while(|&b| b != 0)
        .map(|b| b as char)
        .collect::<String>();
    let t = s.trim().to_string();
    if t.is_empty() {
        None
    } else {
        Some(t)
    }
}

fn join_id(buf: &[u8], vendor: u32, product: u32) -> String {
    let mut parts = Vec::new();
    if let Some(v) = cstr_at(buf, vendor) {
        parts.push(v);
    }
    if let Some(p) = cstr_at(buf, product) {
        parts.push(p);
    }
    parts.join(" ")
}

fn windows_dir_letter() -> Option<char> {
    let mut buf = [0u16; 260];
    let n = unsafe { GetWindowsDirectoryW(Some(&mut buf)) };
    if n == 0 {
        return None;
    }
    String::from_utf16_lossy(&buf[..n as usize]).chars().next()
}

fn extents_disk_numbers(letter: char) -> Vec<u32> {
    let Ok(h) = open_query(&volume_path(letter)) else {
        return Vec::new();
    };
    let mut buf = vec![0u8; 1024];
    if !ioctl(
        h.0,
        IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
        None,
        0,
        buf.as_mut_ptr(),
        buf.len() as u32,
    ) {
        return Vec::new();
    }
    let n = u32_at(&buf, 0) as usize;
    let mut out = Vec::new();
    for i in 0..n.min(16) {
        let off = 4 + i * 24;
        if off + 4 <= buf.len() {
            out.push(u32_at(&buf, off));
        }
    }
    out
}

pub fn enumerate_disks() -> Result<Vec<DiskInfo>, String> {
    let mut disks = Vec::new();
    let mut first_err: Option<(u32, u32)> = None;
    for i in 0..32u32 {
        let path = physical_path(i);
        match open_query(&path) {
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
                continue;
            }
        }
    }
    if disks.is_empty() {
        let extra = match first_err {
            Some((i, 5)) => format!(
                "PD{i} acceso denegado (5). Ejecuta el .exe como administrador (UAC)."
            ),
            Some((i, e)) => format!("PD{i} error Win32 {e}."),
            None => "No se pudo leer el tamaño de ningún PhysicalDrive.".into(),
        };
        return Err(extra);
    }
    let mask = unsafe { GetLogicalDrives() };
    for i in 0..26u32 {
        if mask & (1 << i) == 0 {
            continue;
        }
        let letter = char::from(b'A' + i as u8);
        let root = wide_z(&format!(r"{letter}:\\"));
        let dtype = unsafe { GetDriveTypeW(pcw(&root)) };
        if dtype == 5 {
            continue;
        }
        for n in extents_disk_numbers(letter) {
            if let Some(d) = disks.iter_mut().find(|d| d.index == n) {
                if !d.letters.contains(&letter) {
                    d.letters.push(letter);
                }
            }
        }
    }
    if let Some(sys) = windows_dir_letter() {
        let sys_disks = extents_disk_numbers(sys);
        for d in disks.iter_mut() {
            if sys_disks.contains(&d.index) || d.letters.contains(&sys) {
                d.is_system = true;
            }
        }
    }
    disks.sort_by_key(|d| d.index);
    Ok(disks)
}
