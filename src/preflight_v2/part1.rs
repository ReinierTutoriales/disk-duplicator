use crate::engine_impl::{self, state_dir_for, CopyOpts, DestPhase, FileInfo, JobState};
use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use walkdir::WalkDir;

const MIN_FREE_RESERVE: u64 = 1024 * 1024 * 1024;
const MAX_FREE_RESERVE: u64 = 16 * 1024 * 1024 * 1024;
const RESERVE_PERCENT: u64 = 1;
const VERIFY_BUF: usize = 4 * 1024 * 1024;

type PlannedFile = FileInfo;

#[derive(Clone, Debug, Default)]
struct DestinationPlan {
    new_files: u64,
    replace_files: u64,
    skipped_files: u64,
    bytes_to_write: u64,
    peak_extra_space: u64,
    available_space: u64,
    reserve_space: u64,
}

#[derive(Clone, Debug)]
struct PreflightResult {
    files: Arc<Vec<PlannedFile>>,
    dirs: Arc<Vec<PathBuf>>,
    plans: Vec<DestinationPlan>,
}

fn metadata_mtime_ns(meta: &fs::Metadata) -> u128 {
    meta.modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_nanos())
        .unwrap_or(0)
}

fn scan_source(root: &Path) -> Result<(Vec<PlannedFile>, Vec<PathBuf>), String> {
    let mut files = Vec::new();
    let mut dirs = Vec::new();

    for entry in WalkDir::new(root).follow_links(false) {
        let entry = entry.map_err(|e| format!("origen: {e}"))?;
        if entry.depth() == 0 { continue; }

        let rel = entry
            .path()
            .strip_prefix(root)
            .map_err(|e| e.to_string())?
            .to_path_buf();
        let ft = entry.file_type();

        if ft.is_symlink() {
            return Err(format!(
                "El origen contiene un enlace simbólico que no puede duplicarse 1:1 de forma segura: {}.",
                entry.path().display()
            ));
        }

        if ft.is_dir() {
            dirs.push(rel);
            continue;
        }

        if !ft.is_file() {
            return Err(format!(
                "El origen contiene una entrada especial no compatible: {}.",
                entry.path().display()
            ));
        }

        let meta = entry
            .metadata()
            .map_err(|e| format!("metadata {}: {e}", entry.path().display()))?;
        File::open(entry.path())
            .map_err(|e| format!("No se puede leer {}: {e}", entry.path().display()))?;

        // No filename/extension filters: every regular file is part of the copy plan.
        files.push(PlannedFile {
            rel,
            size: meta.len(),
            mtime_ns: metadata_mtime_ns(&meta),
        });
    }

    files.sort_by(|a, b| a.rel.cmp(&b.rel));
    dirs.sort();
    Ok((files, dirs))
}

fn state_key(info: &PlannedFile) -> String {
    let mut hex = String::new();
    for b in info.rel.to_string_lossy().as_bytes() {
        hex.push_str(&format!("{b:02x}"));
    }
    format!("{hex}|{}|{}", info.size, info.mtime_ns)
}

fn state_path(dest: &Path) -> PathBuf {
    state_dir_for(dest).join("completed.jsonl")
}

fn load_completed(dest: &Path) -> HashSet<String> {
    let Ok(text) = fs::read_to_string(state_path(dest)) else {
        return HashSet::new();
    };
    text.lines()
        .filter_map(|line| {
            let (_, rest) = line.split_once("\"key\":\"")?;
            let (key, _) = rest.split_once('"')?;
            Some(key.to_owned())
        })
        .collect()
}

fn rewrite_completed(dest: &Path, keys: &HashSet<String>) -> Result<(), String> {
    let dir = state_dir_for(dest);
    fs::create_dir_all(&dir).map_err(|e| format!("state mkdir {}: {e}", dir.display()))?;
    let path = state_path(dest);
    let tmp = dir.join("completed.jsonl.preflight");

    let result = (|| {
        let mut f = File::create(&tmp)
            .map_err(|e| format!("state temp {}: {e}", tmp.display()))?;
        let mut ordered: Vec<&String> = keys.iter().collect();
        ordered.sort();
        for key in ordered {
            writeln!(f, "{{\"key\":\"{key}\"}}")
                .map_err(|e| format!("state write: {e}"))?;
        }
        f.sync_data().map_err(|e| format!("state sync: {e}"))?;
        drop(f);

        if path.exists() {
            fs::remove_file(&path)
                .map_err(|e| format!("state replace {}: {e}", path.display()))?;
        }
        fs::rename(&tmp, &path)
            .map_err(|e| format!("state commit {}: {e}", path.display()))?;
        Ok::<(), String>(())
    })();

    if result.is_err() { let _ = fs::remove_file(&tmp); }
    result
}

fn same_enough(src: &Path, dst: &Path) -> bool {
    let (Ok(a), Ok(b)) = (fs::metadata(src), fs::metadata(dst)) else { return false; };
    if !a.is_file() || !b.is_file() || a.len() != b.len() { return false; }
    match (a.modified(), b.modified()) {
        (Ok(x), Ok(y)) => x == y,
        _ => false,
    }
}

fn normalize_completed_state(
    source: &Path,
    dest: &Path,
    files: &[PlannedFile],
) -> Result<HashSet<String>, String> {
    let loaded = load_completed(dest);
    if loaded.is_empty() { return Ok(loaded); }

    let mut valid = HashSet::new();
    for info in files {
        let key = state_key(info);
        if loaded.contains(&key) && same_enough(&source.join(&info.rel), &dest.join(&info.rel)) {
            valid.insert(key);
        }
    }

    if valid != loaded {
        rewrite_completed(dest, &valid)?;
    }
    Ok(valid)
}

fn transient_id(path: &Path) -> String {
    let hash = blake3::hash(path.to_string_lossy().as_bytes()).to_hex().to_string();
    hash[..24].to_owned()
}

fn part_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root).join("tmp").join(format!("{}.part", transient_id(dst)))
}

fn backup_path(dest_root: &Path, dst: &Path) -> PathBuf {
    state_dir_for(dest_root).join("tmp").join(format!("{}.bak", transient_id(dst)))
}

fn cleanup_owned_stale_files(dest: &Path, files: &[PlannedFile]) -> Result<(), String> {
    for info in files {
        let dst = dest.join(&info.rel);
        let tmp = part_path(dest, &dst);
        if tmp.exists() {
            fs::remove_file(&tmp)
                .map_err(|e| format!("No se pudo limpiar {}: {e}", tmp.display()))?;
        }

        let backup = backup_path(dest, &dst);
        if backup.exists() {
            if dst.exists() {
                fs::remove_file(&backup)
                    .map_err(|e| format!("No se pudo limpiar backup {}: {e}", backup.display()))?;
            } else {
                fs::rename(&backup, &dst)
                    .map_err(|e| format!("No se pudo restaurar backup {}: {e}", backup.display()))?;
            }
        }
    }
    Ok(())
}

fn writable_probe(dest: &Path) -> Result<(), String> {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let probe = dest.join(format!(".disk-duplicator-write-test-{}-{stamp}", std::process::id()));
    let result = (|| {
        let mut f = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&probe)
            .map_err(|e| format!("No se puede escribir en {}: {e}", dest.display()))?;
        f.write_all(b"disk-duplicator")
            .map_err(|e| format!("Prueba de escritura en {}: {e}", dest.display()))?;
        f.sync_data()
            .map_err(|e| format!("Prueba de sincronización en {}: {e}", dest.display()))?;
        Ok::<(), String>(())
    })();
    let _ = fs::remove_file(&probe);
    result
}

fn round_up(value: u64, granularity: u64) -> u64 {
    if value == 0 || granularity <= 1 { return value; }
    value
        .saturating_add(granularity - 1)
        .checked_div(granularity)
        .unwrap_or(u64::MAX)
        .saturating_mul(granularity)
}

fn reserve_for_volume(total: u64) -> u64 {
    let percent = total.saturating_mul(RESERVE_PERCENT) / 100;
    MIN_FREE_RESERVE.max(percent.min(MAX_FREE_RESERVE))
}

fn validate_destination_layout(dest: &Path, rel: &Path) -> Result<(), String> {
    let components: Vec<Component<'_>> = rel.components().collect();
    if components.is_empty() {
        return Err("Ruta relativa vacía en el plan de copia.".into());
    }

    let count = components.len();
    let mut current = dest.to_path_buf();
    for (index, component) in components.into_iter().enumerate() {
        match component {
            Component::Normal(name) => current.push(name),
            _ => return Err(format!("Ruta relativa no segura: {}", rel.display())),
        }

        let meta = match fs::symlink_metadata(&current) {
            Ok(meta) => meta,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => continue,
            Err(e) => return Err(format!("No se pudo inspeccionar {}: {e}", current.display())),
        };

        if meta.file_type().is_symlink() {
            return Err(format!(
                "No se permite escribir a través del enlace simbólico {}.",
                current.display()
            ));
        }

        let is_last = index + 1 == count;
        if is_last && meta.is_dir() {
            return Err(format!(
                "Conflicto en destino: {} es una carpeta pero el origen contiene un archivo en esa ruta.",
                current.display()
            ));
        }
        if !is_last && !meta.is_dir() {
            return Err(format!(
                "Conflicto en destino: {} debe ser una carpeta para crear {}.",
                current.display(),
                rel.display()
            ));
        }
    }
    Ok(())
}

fn validate_destination_directories(dest: &Path, dirs: &[PathBuf]) -> Result<(), String> {
    for rel in dirs {
        let path = dest.join(rel);
        match fs::symlink_metadata(&path) {
            Ok(meta) if meta.is_dir() && !meta.file_type().is_symlink() => {}
            Ok(_) => return Err(format!("Conflicto de carpeta en destino: {}.", path.display())),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(format!("No se pudo inspeccionar {}: {e}", path.display())),
        }
    }
    Ok(())
}

fn destination_file_allocation(dst: &Path, granularity: u64) -> Result<Option<u64>, String> {
    match fs::metadata(dst) {
        Ok(meta) => {
            if !meta.is_file() {
                return Err(format!(
                    "Conflicto en {}: el origen requiere un archivo, pero el destino contiene otro tipo de entrada.",
                    dst.display()
                ));
            }
            Ok(Some(round_up(meta.len(), granularity)))
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(format!("No se pudo inspeccionar {}: {e}", dst.display())),
    }
}
