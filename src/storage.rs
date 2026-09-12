use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[cfg(windows)]
fn is_reparse(meta: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    meta.file_attributes() & 0x0000_0400 != 0
}

#[cfg(not(windows))]
fn is_reparse(_: &fs::Metadata) -> bool {
    false
}

fn ensure_normal_directory(path: &Path, label: &str) -> Result<(), String> {
    let meta = fs::symlink_metadata(path)
        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;
    if !meta.is_dir() || meta.file_type().is_symlink() || is_reparse(&meta) {
        return Err(format!(
            "{label} no es una carpeta normal o es un enlace/junction/reparse point: {}",
            path.display()
        ));
    }
    Ok(())
}

fn ensure_regular_file(path: &Path, label: &str) -> Result<(), String> {
    let meta = fs::symlink_metadata(path)
        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;
    if !meta.is_file() || meta.file_type().is_symlink() || is_reparse(&meta) {
        return Err(format!(
            "{label} no es un archivo regular seguro: {}",
            path.display()
        ));
    }
    Ok(())
}

fn unique_sibling(path: &Path, suffix: &str) -> PathBuf {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let name = path
        .file_name()
        .map(|name| name.to_string_lossy())
        .unwrap_or_else(|| "repartocopier".into());
    path.with_file_name(format!(
        ".{name}.{}.{}.{}",
        std::process::id(),
        stamp,
        suffix
    ))
}

pub fn atomic_write(path: &Path, data: &[u8], label: &str) -> Result<(), String> {
    let parent = path
        .parent()
        .ok_or_else(|| format!("Ruta inválida para {label}: {}", path.display()))?;
    ensure_normal_directory(parent, "La carpeta contenedora")?;

    if path.exists() {
        ensure_regular_file(path, label)?;
    }

    let tmp = unique_sibling(path, "tmp");
    let backup = unique_sibling(path, "bak");
    let mut preserve_backup = false;
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&tmp)
            .map_err(|e| {
                format!(
                    "No se pudo crear el temporal de {label} {}: {e}",
                    tmp.display()
                )
            })?;
        file.write_all(data)
            .map_err(|e| format!("No se pudo escribir {label}: {e}"))?;
        file.sync_all()
            .map_err(|e| format!("No se pudo sincronizar {label}: {e}"))?;
        drop(file);

        let had_old = path.exists();
        if had_old {
            ensure_regular_file(path, label)?;
            fs::rename(path, &backup).map_err(|e| {
                format!(
                    "No se pudo preparar el reemplazo de {label} {}: {e}",
                    path.display()
                )
            })?;
        }

        match fs::rename(&tmp, path) {
            Ok(()) => {
                if had_old {
                    let _ = fs::remove_file(&backup);
                }
                Ok(())
            }
            Err(commit_err) => {
                if had_old {
                    match fs::rename(&backup, path) {
                        Ok(()) => Err(format!(
                            "No se pudo reemplazar {label}: {commit_err}; se restauró la versión anterior"
                        )),
                        Err(restore_err) => {
                            preserve_backup = true;
                            Err(format!(
                                "CRÍTICO: no se pudo reemplazar {label}: {commit_err}; tampoco restaurar {}: {restore_err}",
                                backup.display()
                            ))
                        }
                    }
                } else {
                    Err(format!("No se pudo finalizar {label}: {commit_err}"))
                }
            }
        }
    })();

    if tmp.exists() {
        let _ = fs::remove_file(&tmp);
    }
    if !preserve_backup && backup.exists() && path.exists() {
        let _ = fs::remove_file(&backup);
    }
    result
}

pub fn read_regular_file(path: &Path, max_bytes: u64, label: &str) -> Result<Vec<u8>, String> {
    ensure_regular_file(path, label)?;
    let meta = fs::metadata(path).map_err(|e| {
        format!(
            "No se pudo leer metadata de {label} {}: {e}",
            path.display()
        )
    })?;
    if meta.len() > max_bytes {
        return Err(format!(
            "{label} es demasiado grande (máximo {max_bytes} bytes)."
        ));
    }
    let mut file = File::open(path)
        .map_err(|e| format!("No se pudo abrir {label} {}: {e}", path.display()))?;
    let mut data = Vec::with_capacity(meta.len() as usize);
    file.read_to_end(&mut data)
        .map_err(|e| format!("No se pudo leer {label}: {e}"))?;
    Ok(data)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_root(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("repartocopier-{name}-{stamp}"))
    }

    #[test]
    fn atomic_write_replaces_existing_file_without_leftovers() {
        let root = temp_root("atomic");
        fs::create_dir_all(&root).unwrap();
        let path = root.join("settings.conf");
        fs::write(&path, b"old").unwrap();
        atomic_write(&path, b"new", "prueba").unwrap();
        assert_eq!(fs::read(&path).unwrap(), b"new");
        let entries: Vec<_> = fs::read_dir(&root).unwrap().collect();
        assert_eq!(entries.len(), 1);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn atomic_write_rejects_directory_as_target() {
        let root = temp_root("reject-dir");
        fs::create_dir_all(root.join("target")).unwrap();
        let err = atomic_write(&root.join("target"), b"x", "prueba").unwrap_err();
        assert!(err.contains("archivo regular"));
        let _ = fs::remove_dir_all(root);
    }
}
