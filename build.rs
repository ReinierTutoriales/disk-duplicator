use std::{fs, path::Path};

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

fn main() {
    const ICON_B64: &str = "assets/RepartoCopier.ico.b64";
    const ICON: &str = "assets/RepartoCopier.ico";

    println!("cargo:rerun-if-changed=app.manifest");
    println!("cargo:rerun-if-changed=app.rc");
    println!("cargo:rerun-if-changed={ICON_B64}");

    let encoded = fs::read_to_string(ICON_B64).expect("read RepartoCopier icon source");
    let decoded = decode_base64(&encoded).expect("decode RepartoCopier icon source");
    if let Some(parent) = Path::new(ICON).parent() {
        fs::create_dir_all(parent).expect("create icon directory");
    }
    fs::write(ICON, decoded).expect("write RepartoCopier.ico");

    embed_resource::compile("app.rc", embed_resource::NONE);
}
