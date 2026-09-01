use windows::core::PCWSTR;

pub fn wide_z(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

pub fn pcw(buf: &[u16]) -> PCWSTR {
    PCWSTR(buf.as_ptr())
}

pub fn physical_path(index: u32) -> String {
    format!(r"\\.\PhysicalDrive{index}")
}

pub fn volume_path(letter: char) -> String {
    format!(r"\\.\{letter}:")
}
