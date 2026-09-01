use windows::Win32::System::SystemInformation::*;
use windows::Win32::Foundation::*;
use windows::core::*;
use super::types::DiskInfo;

pub fn mark_system_disk(disks: &mut Vec<DiskInfo>) -> Result<()> {
    let mut buffer = vec![0u16; 260];
    let len = unsafe { GetWindowsDirectoryW(PWSTR(buffer.as_mut_ptr()), buffer.len() as u32) };
    
    if len == 0 {
        return Err(Error::from_win32());
    }
    
    let windows_dir = String::from_utf16_lossy(&buffer[..len as usize]);
    let system_drive_letter = windows_dir.chars().next().unwrap_or('C');
    
    for disk in disks.iter_mut() {
        if disk.volume_letters.contains(&system_drive_letter) {
            disk.is_system_disk = true;
        }
    }
    
    Ok(())
}
