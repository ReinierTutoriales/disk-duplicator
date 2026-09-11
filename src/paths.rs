use std::path::{Path, PathBuf};

const STATE_DIR_NAME: &str = ".disk-duplicator-state";
const STATE_ID_HEX: usize = 32;
const TRANSIENT_ID_HEX: usize = 32;
const LEGACY_STATE_ID_HEX: usize = 16;
const LEGACY_TRANSIENT_ID_HEX: usize = 24;

fn native_path_bytes(path: &Path) -> Vec<u8> {
    #[cfg(windows)]
    {
        use std::char::decode_utf16;
        use std::os::windows::ffi::OsStrExt;

        let mut normalized = Vec::new();
        for unit in decode_utf16(path.as_os_str().encode_wide()) {
            match unit {
                Ok(ch) => {
                    for lower in ch.to_lowercase() {
                        let mut buf = [0u16; 2];
                        for encoded in lower.encode_utf16(&mut buf) {
                            normalized.extend_from_slice(&encoded.to_le_bytes());
                        }
                    }
                }
                Err(err) => normalized.extend_from_slice(&err.unpaired_surrogate().to_le_bytes()),
            }
        }
        normalized
    }

    #[cfg(not(windows))]
    {
        path.to_string_lossy().as_bytes().to_vec()
    }
}

// Compatibility with the immediately previous 32-hex format, which hashed the
// exact native Windows path bytes before IDs became case-insensitive.
fn previous_native_path_bytes(path: &Path) -> Vec<u8> {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        path.as_os_str()
            .encode_wide()
            .flat_map(u16::to_le_bytes)
            .collect()
    }

    #[cfg(not(windows))]
    {
        path.to_string_lossy().as_bytes().to_vec()
    }
}

fn digest_hex(bytes: &[u8], chars: usize) -> String {
    let full = blake3::hash(bytes).to_hex().to_string();
    full[..chars].to_owned()
}

fn legacy_digest_hex(path: &Path, chars: usize) -> String {
    digest_hex(path.to_string_lossy().as_bytes(), chars)
}

pub(crate) fn state_id(dest: &Path) -> String {
    digest_hex(&native_path_bytes(dest), STATE_ID_HEX)
}

pub(crate) fn transient_id(path: &Path) -> String {
    digest_hex(&native_path_bytes(path), TRANSIENT_ID_HEX)
}

fn previous_state_id(dest: &Path) -> String {
    digest_hex(&previous_native_path_bytes(dest), STATE_ID_HEX)
}

fn previous_transient_id(path: &Path) -> String {
    digest_hex(&previous_native_path_bytes(path), TRANSIENT_ID_HEX)
}

fn legacy_state_id(dest: &Path) -> String {
    legacy_digest_hex(dest, LEGACY_STATE_ID_HEX)
}

fn legacy_transient_id(path: &Path) -> String {
    legacy_digest_hex(path, LEGACY_TRANSIENT_ID_HEX)
}

pub(crate) fn state_dir_for(dest: &Path) -> PathBuf {
    let parent = dest.parent().unwrap_or(dest);
    parent.join(STATE_DIR_NAME).join(state_id(dest))
}

fn previous_state_dir_for(dest: &Path) -> PathBuf {
    let parent = dest.parent().unwrap_or(dest);
    parent.join(STATE_DIR_NAME).join(previous_state_id(dest))
}

pub(crate) fn legacy_state_dir_for(dest: &Path) -> PathBuf {
    let parent = dest.parent().unwrap_or(dest);
    parent.join(STATE_DIR_NAME).join(legacy_state_id(dest))
}

pub(crate) fn prepare_state_dir(dest: &Path) -> Result<PathBuf, String> {
    let current = state_dir_for(dest);
    if current.exists() {
        return Ok(current);
    }

    // Prefer the immediately previous 32-hex representation before falling
    // back to the older 16-hex representation. This preserves resume data
    // across the case-normalization upgrade.
    for previous in [previous_state_dir_for(dest), legacy_state_dir_for(dest)] {
        if previous == current || !previous.exists() {
            continue;
        }
        std::fs::create_dir_all(current.parent().unwrap_or(&current))
            .map_err(|e| format!("No se pudo preparar el directorio de estado: {e}"))?;
        std::fs::rename(&previous, &current).map_err(|e| {
            format!(
                "No se pudo migrar el estado anterior {} a {}: {e}",
                previous.display(),
                current.display()
            )
        })?;
        break;
    }

    Ok(current)
}

pub(crate) fn state_path(dest: &Path) -> PathBuf {
    state_dir_for(dest).join("completed.jsonl")
}

pub(crate) fn state_rewrite_tmp_path(dest: &Path) -> PathBuf {
    state_dir_for(dest).join("completed.jsonl.preflight")
}

pub(crate) fn state_rewrite_backup_path(dest: &Path) -> PathBuf {
    state_dir_for(dest).join("completed.jsonl.preflight.bak")
}

pub(crate) fn manifest_path(dest: &Path) -> PathBuf {
    state_dir_for(dest).join("manifest.b3")
}

pub(crate) fn part_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root)
        .join("tmp")
        .join(format!("{}.part", transient_id(dst)))
}

pub(crate) fn backup_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root)
        .join("tmp")
        .join(format!("{}.bak", transient_id(dst)))
}

pub(crate) fn previous_part_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root)
        .join("tmp")
        .join(format!("{}.part", previous_transient_id(dst)))
}

pub(crate) fn previous_backup_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root)
        .join("tmp")
        .join(format!("{}.bak", previous_transient_id(dst)))
}

pub(crate) fn legacy_part_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root)
        .join("tmp")
        .join(format!("{}.part", legacy_transient_id(dst)))
}

pub(crate) fn legacy_backup_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root)
        .join("tmp")
        .join(format!("{}.bak", legacy_transient_id(dst)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hardened_ids_use_128_bits() {
        let path = Path::new(r"C:\\example\\target");
        assert_eq!(state_id(path).len(), 32);
        assert_eq!(transient_id(path).len(), 32);
    }

    #[cfg(windows)]
    #[test]
    fn ids_are_case_insensitive_on_windows() {
        let upper = Path::new(r"E:\\Backup\\Folder\\File.ISO");
        let lower = Path::new(r"e:\\backup\\folder\\file.iso");
        assert_eq!(state_id(upper), state_id(lower));
        assert_eq!(transient_id(upper), transient_id(lower));
    }

    #[cfg(windows)]
    #[test]
    fn previous_ids_remain_distinct_for_migration() {
        let upper = Path::new(r"E:\\Backup\\Folder\\File.ISO");
        let lower = Path::new(r"e:\\backup\\folder\\file.iso");
        assert_ne!(previous_state_id(upper), previous_state_id(lower));
        assert_ne!(previous_transient_id(upper), previous_transient_id(lower));
        assert_eq!(state_id(upper), state_id(lower));
        assert_eq!(transient_id(upper), transient_id(lower));
    }
}