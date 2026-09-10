use std::{env, fs, path::Path};

fn decode_base64(input: &str) -> Result<Vec<u8>, String> {
    let mut out = Vec::with_capacity(input.len() * 3 / 4);
    let mut quartet = [0u8; 4];
    let mut n = 0usize;

    for byte in input.bytes().filter(|b| !b.is_ascii_whitespace()) {
        let value = match byte {
            b'A'..=b'Z' => byte - b'A',
            b'a'..=b'z' => byte - b'a' + 26,
            b'0'..=b'9' => byte - b'0' + 52,
            b'+' => 62,
            b'/' => 63,
            b'=' => 64,
            _ => return Err(format!("invalid base64 byte: {byte}")),
        };
        quartet[n] = value;
        n += 1;

        if n == 4 {
            if quartet[0] == 64 || quartet[1] == 64 {
                return Err("invalid base64 padding".into());
            }
            out.push((quartet[0] << 2) | (quartet[1] >> 4));
            if quartet[2] != 64 {
                out.push((quartet[1] << 4) | (quartet[2] >> 2));
                if quartet[3] != 64 {
                    out.push((quartet[2] << 6) | quartet[3]);
                }
            }
            n = 0;
        }
    }

    if n != 0 {
        return Err("incomplete base64 input".into());
    }
    Ok(out)
}

fn read_u32_le(bytes: &[u8]) -> u32 {
    u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]])
}

fn largest_png_from_ico(ico: &[u8]) -> Result<&[u8], String> {
    if ico.len() < 6 || ico[0..2] != [0, 0] || ico[2..4] != [1, 0] {
        return Err("invalid ICO header".into());
    }

    let count = u16::from_le_bytes([ico[4], ico[5]]) as usize;
    let entries_end = 6usize
        .checked_add(count.checked_mul(16).ok_or("ICO entry overflow")?)
        .ok_or("ICO header overflow")?;
    if ico.len() < entries_end {
        return Err("truncated ICO directory".into());
    }

    let mut best: Option<(u32, &[u8])> = None;
    for i in 0..count {
        let entry = 6 + i * 16;
        let width = if ico[entry] == 0 { 256 } else { ico[entry] as u32 };
        let height = if ico[entry + 1] == 0 { 256 } else { ico[entry + 1] as u32 };
        let size = read_u32_le(&ico[entry + 8..entry + 12]) as usize;
        let offset = read_u32_le(&ico[entry + 12..entry + 16]) as usize;
        let end = offset.checked_add(size).ok_or("ICO image overflow")?;
        if end > ico.len() {
            return Err("truncated ICO image".into());
        }
        let image = &ico[offset..end];
        if image.starts_with(b"\x89PNG\r\n\x1a\n") {
            let area = width.saturating_mul(height);
            if best.as_ref().is_none_or(|(best_area, _)| area > *best_area) {
                best = Some((area, image));
            }
        }
    }

    best.map(|(_, image)| image)
        .ok_or_else(|| "ICO does not contain a PNG frame".into())
}

fn validate_windows_version_metadata(app_rc: &str) {
    let package_version = env::var("CARGO_PKG_VERSION").expect("CARGO_PKG_VERSION");
    let numeric = package_version.split('-').next().unwrap_or(&package_version);
    let mut parts = numeric.split('.');
    let major = parts.next().unwrap_or("0");
    let minor = parts.next().unwrap_or("0");
    let patch = parts.next().unwrap_or("0");
    let tuple = format!("{major},{minor},{patch},0");

    let required = [
        format!("FILEVERSION {tuple}"),
        format!("PRODUCTVERSION {tuple}"),
        format!("VALUE \"FileVersion\", \"{package_version}\\0\""),
        format!("VALUE \"ProductVersion\", \"{package_version}\\0\""),
    ];

    for expected in required {
        assert!(
            app_rc.contains(&expected),
            "app.rc version metadata is out of sync with Cargo.toml: missing {expected}"
        );
    }
}

fn main() {
    const ICON_B64: &str = "assets/RepartoCopier.ico.b64";
    const ICON: &str = "assets/RepartoCopier.ico";
    const RUNTIME_ICON: &str = "assets/RepartoCopier-runtime.png";

    println!("cargo:rerun-if-changed=app.manifest");
    println!("cargo:rerun-if-changed=app.rc");
    println!("cargo:rerun-if-changed={ICON_B64}");
    println!("cargo:rerun-if-env-changed=CARGO_PKG_VERSION");

    let encoded = fs::read_to_string(ICON_B64).expect("read RepartoCopier icon source");
    let decoded = decode_base64(&encoded).expect("decode RepartoCopier icon source");
    if let Some(parent) = Path::new(ICON).parent() {
        fs::create_dir_all(parent).expect("create icon directory");
    }
    fs::write(ICON, &decoded).expect("write RepartoCopier.ico");

    let runtime_png = largest_png_from_ico(&decoded).expect("extract runtime icon PNG from ICO");
    fs::write(RUNTIME_ICON, runtime_png).expect("write RepartoCopier runtime PNG");

    let app_rc = fs::read_to_string("app.rc").expect("read app.rc");
    validate_windows_version_metadata(&app_rc);

    embed_resource::compile("app.rc", embed_resource::NONE);
}
