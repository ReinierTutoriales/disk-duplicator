use std::path::{Path, PathBuf};

const STATE_DIR_NAME: &str = ".disk-duplicator-state";
const STATE_ID_HEX: usize = 32;
const TRANSIENT_ID_HEX: usize = 32;
const LEGACY_STATE_ID_HEX: usize = 16;
const LEGACY_TRANSIENT_ID_HEX: usize = 24;

#[cfg(windows)]
fn is_reparse_point(meta: &std::fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
    meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

#[cfg(not(windows))]
fn is_reparse_point(_meta: &std::fs::Metadata) -> bool {
    false
}

fn ensure_normal_dir(path: &Path, label: &str) -> Result<(), String> {
    let meta = std::fs::symlink_metadata(path)
        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;
    if !meta.is_dir() || meta.file_type().is_symlink() || is_reparse_point(&meta) {
        return Err(format!(
            "{label} no es un directorio normal o es un enlace/junction/reparse point: {}.",
            path.display()
        ));
    }
    Ok(())
}

fn exact_path_bytes(path: &Path) -> Vec<u8> {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        path.as_os_str()
            .encode_wide()
            .flat_map(u16::to_le_bytes)
            .collect()
    }

    #[cfg(unix)]
    {
        use std::os::unix::ffi::OsStrExt;
        path.as_os_str().as_bytes().to_vec()
    }

    #[cfg(all(not(windows), not(unix)))]
    {
        path.to_string_lossy().as_bytes().to_vec()
    }
}

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

    #[cfg(unix)]
    {
        exact_path_bytes(path)
    }

    #[cfg(all(not(windows), not(unix)))]
    {
        path.to_string_lossy().as_bytes().to_vec()
    }
}

fn previous_native_path_bytes(path: &Path) -> Vec<u8> {
    #[cfg(windows)]
    {
        exact_path_bytes(path)
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

pub(crate) fn persisted_path_key(path: &Path) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let bytes = exact_path_bytes(path);
    let mut out = String::with_capacity(3 + bytes.len() * 2);
    out.push_str("p2:");
    for byte in bytes {
        out.push(HEX[(byte >> 4) as usize] as char);
        out.push(HEX[(byte & 0x0f) as usize] as char);
    }
    out
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
    let container = current
        .parent()
        .ok_or_else(|| "La ruta del directorio de estado no tiene padre.".to_owned())?;

    if container.exists() {
        ensure_normal_dir(container, "El contenedor de estado")?;
    } else {
        std::fs::create_dir(container).map_err(|e| {
            format!(
                "No se pudo crear el contenedor de estado {}: {e}",
                container.display()
            )
        })?;
        ensure_normal_dir(container, "El contenedor de estado")?;
    }

    if current.exists() {
        ensure_normal_dir(&current, "El directorio de estado")?;
        return Ok(current);
    }

    for previous in [previous_state_dir_for(dest), legacy_state_dir_for(dest)] {
        if previous == current || !previous.exists() {
            continue;
        }
        ensure_normal_dir(&previous, "El directorio de estado anterior")?;
        std::fs::rename(&previous, &current).map_err(|e| {
            format!(
                "No se pudo migrar el estado anterior {} a {}: {e}",
                previous.display(),
                current.display()
            )
        })?;
        break;
    }

    if !current.exists() {
        std::fs::create_dir(&current).map_err(|e| {
            format!(
                "No se pudo crear el directorio de estado {}: {e}",
                current.display()
            )
        })?;
    }
    ensure_normal_dir(&current, "El directorio de estado")?;
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
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn hardened_ids_use_128_bits() {
        let path = Path::new(r"C:\\example\\target");
        assert_eq!(state_id(path).len(), 32);
        assert_eq!(transient_id(path).len(), 32);
    }

    #[test]
    fn persisted_path_keys_are_versioned() {
        assert!(persisted_path_key(Path::new("folder/file.bin")).starts_with("p2:"));
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

    #[cfg(windows)]
    #[test]
    fn prepare_state_dir_migrates_previous_32_hex_directory() {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!("disk-duplicator-path-migration-{stamp}"));
        std::fs::create_dir_all(&root).unwrap();
        let dest = root.join("CopyWithUPPERCase");
        std::fs::create_dir_all(&dest).unwrap();

        let previous = previous_state_dir_for(&dest);
        let current = state_dir_for(&dest);
        assert_ne!(
            previous, current,
            "la prueba necesita que ambos formatos difieran"
        );
        std::fs::create_dir_all(&previous).unwrap();
        std::fs::write(previous.join("completed.jsonl"), b"resume-data").unwrap();

        let prepared = prepare_state_dir(&dest).unwrap();
        assert_eq!(prepared, current);
        assert!(!previous.exists());
        assert_eq!(
            std::fs::read(current.join("completed.jsonl")).unwrap(),
            b"resume-data"
        );
        let _ = std::fs::remove_dir_all(root);
    }
}
