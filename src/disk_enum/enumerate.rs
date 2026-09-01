use windows::Win32::Devices::DeviceAndDriverInstallation::*;
use windows::Win32::Foundation::*;
use windows::Win32::Storage::FileSystem::*;
use windows::core::*;
use std::path::PathBuf;
use super::types::{DiskInfo, BusType};

pub fn enumerate_physical_disks() -> Result<Vec<DiskInfo>> {
    let mut disks = Vec::new();
    
    let disk_guid = GUID_DEVCLASS_DISKDRIVE;
    
    unsafe {
        let dev_info_set = SetupDiGetClassDevsW(
            Some(&disk_guid),
            None,
            None,
            DIGCF_PRESENT,
        )?;
        
        let mut index: u32 = 0;
        let mut dev_info_data = SP_DEVINFO_DATA {
            cbSize: std::mem::size_of::<SP_DEVINFO_DATA>() as u32,
            ClassGuid: GUID::zeroed(),
            DevInst: 0,
            Reserved: 0,
        };
        
        while SetupDiEnumDeviceInfo(dev_info_set, index, &mut dev_info_data).is_ok() {
            if let Ok(disk) = extract_disk_info(dev_info_set, &dev_info_data, index) {
                disks.push(disk);
            }
            index += 1;
        }
        
        SetupDiDestroyDeviceInfoList(dev_info_set)?;
    }
    
    super::filter::mark_system_disk(&mut disks)?;
    
    Ok(disks)
}

unsafe fn extract_disk_info(
    dev_info_set: HDEVINFO,
    dev_info_data: &SP_DEVINFO_DATA,
    index: u32,
) -> Result<DiskInfo> {
    let physical_path = get_device_property_string(
        dev_info_set,
        dev_info_data,
        SPDRP_PHYSICAL_DEVICE_OBJECT_NAME,
    )?;
    
    let drive_number = extract_drive_number(&physical_path)?;
    
    let path_wide: Vec<u16> = physical_path.encode_utf16().chain(std::iter::once(0)).collect();
    let handle = CreateFileW(
        PCWSTR(path_wide.as_ptr()),
        0,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        None,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        None,
    )?;
    
    let mut geometry: DISK_GEOMETRY_EX = std::mem::zeroed();
    let mut bytes_returned = 0u32;
    DeviceIoControl(
        handle,
        IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
        None,
        0,
        Some(&mut geometry as *mut _ as *mut _),
        std::mem::size_of::<DISK_GEOMETRY_EX>() as u32,
        Some(&mut bytes_returned),
        None,
    )?;
    
    let size_bytes = geometry.DiskSize.QuadPart() as u64;
    let sector_size = geometry.Geometry.BytesPerSector;
    
    CloseHandle(handle)?;
    
    let model = get_device_property_string(dev_info_set, dev_info_data, SPDRP_FRIENDLYNAME)
        .unwrap_or_else(|_| "Unknown".to_string());
    
    let serial = get_device_property_string(dev_info_set, dev_info_data, SPDRP_HARDWAREID)
        .unwrap_or_else(|_| "N/A".to_string());
    
    let bus_type = determine_bus_type(dev_info_set, dev_info_data);
    
    Ok(DiskInfo {
        physical_path: PathBuf::from(physical_path),
        index: drive_number,
        model,
        serial,
        size_bytes,
        sector_size,
        bus_type,
        is_removable: bus_type == BusType::Usb,
        is_system_disk: false,
        volume_letters: Vec::new(),
    })
}

fn get_device_property_string(
    dev_info_set: HDEVINFO,
    dev_info_data: &SP_DEVINFO_DATA,
    property: u32,
) -> Result<String> {
    let mut required_size = 0u32;
    
    unsafe {
        let _ = SetupDiGetDeviceRegistryPropertyW(
            dev_info_set,
            dev_info_data,
            property,
            None,
            None,
            Some(&mut required_size),
        );
        
        if required_size == 0 {
            return Err(Error::from_win32());
        }
        
        let mut buffer = vec![0u8; required_size as usize];
        
        SetupDiGetDeviceRegistryPropertyW(
            dev_info_set,
            dev_info_data,
            property,
            None,
            Some(buffer.as_mut_ptr()),
            Some(&mut required_size),
        )?;
        
        let wide_str: Vec<u16> = buffer
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .take_while(|&c| c != 0)
            .collect();
        
        Ok(String::from_utf16_lossy(&wide_str))
    }
}

fn extract_drive_number(path: &str) -> Result<u32> {
    let num_str = path
        .rsplit_once('e')
        .or_else(|| path.rsplit_once('E'))
        .and_then(|(_, n)| n.parse::<u32>().ok())
        .ok_or_else(|| Error::from(ERROR_INVALID_DATA))?;
    
    Ok(num_str)
}

fn determine_bus_type(dev_info_set: HDEVINFO, dev_info_data: &SP_DEVINFO_DATA) -> BusType {
    if let Ok(hw_id) = get_device_property_string(dev_info_set, dev_info_data, SPDRP_HARDWAREID) {
        let hw_id_upper = hw_id.to_uppercase();
        if hw_id_upper.contains("USB") {
            return BusType::Usb;
        } else if hw_id_upper.contains("NVME") {
            return BusType::Nvme;
        } else if hw_id_upper.contains("SATA") || hw_id_upper.contains("ATA") {
            return BusType::Sata;
        }
    }
    BusType::Unknown
}
