use crate::copy_plan::{CopyPlan, MAX_DESTINATIONS};
use crate::storage;
use std::path::{Path, PathBuf};

const MAGIC: &str = "RepartoCopierSession/1";
const MAX_SESSION_BYTES: u64 = 1024 * 1024;

pub type CopySession = CopyPlan;

fn encode_hex(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let bytes = value.as_bytes();
    let mut out = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        out.push(HEX[(byte >> 4) as usize] as char);
        out.push(HEX[(byte & 0x0f) as usize] as char);
    }
    out
}

fn decode_nibble(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        _ => None,
    }
}

fn decode_hex(value: &str, field: &str) -> Result<String, String> {
    let bytes = value.as_bytes();
    if !bytes.len().is_multiple_of(2) {
        return Err(format!("Campo {field} tiene hexadecimal inválido."));
    }
    let mut out = Vec::with_capacity(bytes.len() / 2);
    for pair in bytes.as_chunks::<2>().0 {
        let hi = decode_nibble(pair[0])
            .ok_or_else(|| format!("Campo {field} tiene hexadecimal inválido."))?;
        let lo = decode_nibble(pair[1])
            .ok_or_else(|| format!("Campo {field} tiene hexadecimal inválido."))?;
        out.push((hi << 4) | lo);
    }
    String::from_utf8(out).map_err(|_| format!("Campo {field} no contiene UTF-8 válido."))
}

fn parse_bool(value: &str, field: &str) -> Result<bool, String> {
    match value {
        "0" => Ok(false),
        "1" => Ok(true),
        _ => Err(format!("Campo {field} inválido; se esperaba 0 o 1.")),
    }
}

fn render(session: &CopySession) -> String {
    let mut out = String::new();
    out.push_str(MAGIC);
    out.push('\n');
    out.push_str("source=");
    out.push_str(&encode_hex(session.source.trim()));
    out.push('\n');
    out.push_str(if session.skip_same {
        "skip_same=1\n"
    } else {
        "skip_same=0\n"
    });
    out.push_str(if session.keep_going {
        "keep_going=1\n"
    } else {
        "keep_going=0\n"
    });
    for dest in &session.dests {
        out.push_str("dest=");
        out.push_str(&encode_hex(dest.trim()));
        out.push('\n');
    }
    out
}

fn parse(text: &str) -> Result<CopySession, String> {
    let mut lines = text.lines();
    if lines.next() != Some(MAGIC) {
        return Err("Formato o versión de copia guardada no compatible.".to_owned());
    }

    let mut source = None;
    let mut skip_same = None;
    let mut keep_going = None;
    let mut dests = Vec::new();
    for line in lines {
        if line.is_empty() {
            continue;
        }
        let (key, value) = line
            .split_once('=')
            .ok_or_else(|| "La copia guardada contiene una línea inválida.".to_owned())?;
        match key {
            "source" => {
                if source.is_some() {
                    return Err("La copia guardada contiene más de un origen.".to_owned());
                }
                source = Some(decode_hex(value, "source")?);
            }
            "skip_same" => {
                if skip_same.is_some() {
                    return Err("La copia guardada duplica skip_same.".to_owned());
                }
                skip_same = Some(parse_bool(value, "skip_same")?);
            }
            "keep_going" => {
                if keep_going.is_some() {
                    return Err("La copia guardada duplica keep_going.".to_owned());
                }
                keep_going = Some(parse_bool(value, "keep_going")?);
            }
            "dest" => {
                if dests.len() >= MAX_DESTINATIONS {
                    return Err(format!(
                        "La copia guardada supera el máximo de {MAX_DESTINATIONS} destinos."
                    ));
                }
                dests.push(decode_hex(value, "dest")?);
            }
            _ => return Err(format!("Campo de copia guardada desconocido: {key}.")),
        }
    }

    CopyPlan::new(
        source.ok_or_else(|| "La copia guardada no contiene source.".to_owned())?,
        dests,
        skip_same.ok_or_else(|| "La copia guardada no contiene skip_same.".to_owned())?,
        keep_going.ok_or_else(|| "La copia guardada no contiene keep_going.".to_owned())?,
    )
}

pub fn with_default_extension(path: PathBuf) -> PathBuf {
    if path.extension().is_some() {
        path
    } else {
        path.with_extension("repartocopy")
    }
}

pub fn save(path: &Path, session: &CopySession) -> Result<(), String> {
    let text = render(session);
    storage::atomic_write(path, text.as_bytes(), "la copia guardada")
}

pub fn load(path: &Path) -> Result<CopySession, String> {
    let bytes = storage::read_regular_file(path, MAX_SESSION_BYTES, "la copia guardada")?;
    let text = std::str::from_utf8(&bytes)
        .map_err(|_| "La copia guardada no contiene UTF-8 válido.".to_owned())?;
    parse(text)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn sample() -> CopySession {
        CopySession {
            source: r"C:\Música\Niño\日本語".to_owned(),
            dests: vec![
                r"D:\Copias".to_owned(),
                r"\\servidor\Datos compartidos".to_owned(),
            ],
            skip_same: true,
            keep_going: false,
        }
    }

    #[test]
    fn unicode_and_unc_roundtrip() {
        let original = sample();
        let text = render(&original);
        assert_eq!(parse(&text).unwrap(), original);
        assert!(!text.contains("Música"));
    }

    #[test]
    fn parser_rejects_bad_version_duplicate_fields_and_bad_hex() {
        assert!(parse("RepartoCopierSession/9\n").is_err());
        let valid = render(&sample());
        let duplicated = valid.replacen("skip_same=1\n", "skip_same=1\nskip_same=1\n", 1);
        assert!(parse(&duplicated).unwrap_err().contains("duplica"));
        let bad = valid.replacen("source=", "source=z", 1);
        assert!(parse(&bad).unwrap_err().contains("hexadecimal"));
    }

    #[test]
    fn file_roundtrip_and_default_extension() {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!("repartocopier-session-{stamp}"));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("trabajo.repartocopy");
        let original = sample();
        save(&path, &original).unwrap();
        assert_eq!(load(&path).unwrap(), original);
        assert_eq!(
            with_default_extension(root.join("trabajo"))
                .extension()
                .unwrap(),
            "repartocopy"
        );
        let _ = fs::remove_dir_all(root);
    }
}
