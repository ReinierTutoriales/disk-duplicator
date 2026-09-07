use crate::engine_impl::{self, CopyOpts, JobState};
use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{SystemTime, UNIX_EPOCH};
use walkdir::WalkDir;

const MIN_FREE_RESERVE: u64 = 1024 * 1024 * 1024;
const RESERVE_PERCENT: u64 = 1;

#[derive(Clone, Debug)]
struct PlannedFile {
    rel: PathBuf,
    size: u64,
    mtime_ns: u128,
}

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

fn list_source_files(root: &Path) -> Result<Vec<PlannedFile>, String> {
    let mut files = Vec::new();
    for entry in WalkDir::new(root).follow_links(false) {
        let entry = entry.map_err(|e| format!("origen: {e}"))?;
        if !entry.file_type().is_file() {
            continue;
        }
        let name = entry.file_name().to_string_lossy();
        if name.ends_with(".part") || name == "paquetecopies.b3" {
            continue;
        }
        let meta = entry.metadata().map_err(|e| format!("metadata {}: {e}", entry.path().display()))?;
        File::open(entry.path()).map_err(|e| format!("No se puede leer {}: {e}", entry.path().display()))?;
        let rel = entry
            .path()
            .strip_prefix(root)
            .map_err(|e| e.to_string())?
            .to_path_buf();
        let mtime_ns = meta
            .modified()
            .ok()
            .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        files.push(PlannedFile { rel, size: meta.len(), mtime_ns });
    }
    files.sort_by(|a, b| a.rel.cmp(&b.rel));
    Ok(files)
}

fn state_key(info: &PlannedFile) -> String {
    let mut hex = String::new();
    for b in info.rel.to_string_lossy().as_bytes() {
        hex.push_str(&format!("{b:02x}"));
    }
    format!("{hex}|{}|{}", info.size, info.mtime_ns)
}

fn load_completed(dest: &Path) -> HashSet<String> {
    let path = dest.join(".disk-duplicator").join("completed.jsonl");
    let Ok(text) = fs::read_to_string(path) else {
        return HashSet::new();
    };
    text.lines()
        .filter_map(|line| {
            let (_, rest) = line.split_once("\"key\":\"")?;
            let (key, _) = rest.split_once('\"')?;
            Some(key.to_owned())
        })
        .collect()
}

fn same_enough(src: &Path, dst: &Path) -> bool {
    let Ok(a) = fs::metadata(src) else { return false; };
    let Ok(b) = fs::metadata(dst) else { return false; };
    if a.len() != b.len() {
        return false;
    }
    match (a.modified(), b.modified()) {
        (Ok(x), Ok(y)) => x == y,
        _ => false,
    }
}

fn part_path(dst: &Path) -> PathBuf {
    let mut p = dst.as_os_str().to_os_string();
    p.push(".part");
    PathBuf::from(p)
}

fn cleanup_owned_stale_parts(dest: &Path, files: &[PlannedFile]) -> Result<(), String> {
    for info in files {
        let tmp = part_path(&dest.join(&info.rel));
        if tmp.exists() {
            fs::remove_file(&tmp).map_err(|e| format!("No se pudo limpiar {}: {e}", tmp.display()))?;
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
        f.sync_all()
            .map_err(|e| format!("Prueba de sincronización en {}: {e}", dest.display()))?;
        Ok::<(), String>(())
    })();
    let _ = fs::remove_file(&probe);
    result
}

fn round_up(value: u64, granularity: u64) -> u64 {
    if value == 0 || granularity <= 1 {
        return value;
    }
    value
        .saturating_add(granularity - 1)
        .checked_div(granularity)
        .unwrap_or(u64::MAX)
        .saturating_mul(granularity)
}

fn plan_destination(
    source: &Path,
    dest: &Path,
    files: &[PlannedFile],
    opts: CopyOpts,
) -> Result<DestinationPlan, String> {
    cleanup_owned_stale_parts(dest, files)?;

    let available = fs2::available_space(dest)
        .map_err(|e| format!("No se pudo consultar espacio libre en {}: {e}", dest.display()))?;
    let total = fs2::total_space(dest)
        .map_err(|e| format!("No se pudo consultar capacidad de {}: {e}", dest.display()))?;
    let granularity = fs2::allocation_granularity(dest).unwrap_or(4096).max(1);
    let reserve = MIN_FREE_RESERVE.max(total.saturating_mul(RESERVE_PERCENT) / 100);
    let completed = load_completed(dest);

    let mut plan = DestinationPlan {
        available_space: available,
        reserve_space: reserve,
        ..DestinationPlan::default()
    };
    let mut committed_delta: i128 = 0;
    let mut peak_extra: i128 = 0;

    for info in files {
        let src = source.join(&info.rel);
        let dst = dest.join(&info.rel);
        let key = state_key(info);
        if completed.contains(&key) || (opts.skip_same && same_enough(&src, &dst)) {
            plan.skipped_files += 1;
            continue;
        }

        let new_alloc = round_up(info.size, granularity);
        let old_alloc = fs::metadata(&dst)
            .ok()
            .filter(|m| m.is_file())
            .map(|m| round_up(m.len(), granularity))
            .unwrap_or(0);

        if old_alloc == 0 {
            plan.new_files += 1;
        } else {
            plan.replace_files += 1;
        }
        plan.bytes_to_write = plan.bytes_to_write.saturating_add(info.size);

        let during_temp = committed_delta.saturating_add(new_alloc as i128);
        peak_extra = peak_extra.max(during_temp);
        committed_delta = committed_delta
            .saturating_add(new_alloc as i128)
            .saturating_sub(old_alloc as i128);
    }

    plan.peak_extra_space = peak_extra.max(0).min(u64::MAX as i128) as u64;
    let required_with_reserve = plan.peak_extra_space.saturating_add(plan.reserve_space);
    if available < required_with_reserve {
        let missing = required_with_reserve - available;
        return Err(format!(
            "Espacio insuficiente en {}. Pico requerido: {} bytes + reserva: {} bytes; disponible: {} bytes; faltan: {} bytes.",
            dest.display(),
            plan.peak_extra_space,
            plan.reserve_space,
            available,
            missing
        ));
    }

    Ok(plan)
}

fn canonical_existing(path: &Path, label: &str) -> Result<PathBuf, String> {
    path.canonicalize()
        .map_err(|e| format!("No se pudo resolver {label} {}: {e}", path.display()))
}

fn validate_destinations(source: &Path, dests: &[PathBuf]) -> Result<Vec<PathBuf>, String> {
    let source_canon = canonical_existing(source, "origen")?;
    let mut canonical = Vec::with_capacity(dests.len());

    for dest in dests {
        fs::create_dir_all(dest).map_err(|e| format!("destino {}: {e}", dest.display()))?;
        let d = canonical_existing(dest, "destino")?;
        if d.parent().is_none() {
            return Err(format!("No se permite usar la raíz {} como destino.", d.display()));
        }
        if d == source_canon || d.starts_with(&source_canon) || source_canon.starts_with(&d) {
            return Err(format!("El destino {} se solapa con el origen.", dest.display()));
        }
        writable_probe(&d)?;
        canonical.push(d);
    }

    for i in 0..canonical.len() {
        for j in (i + 1)..canonical.len() {
            if canonical[i] == canonical[j] {
                return Err(format!(
                    "Los destinos {} y {} apuntan a la misma ubicación.",
                    dests[i].display(),
                    dests[j].display()
                ));
            }
            if canonical[i].starts_with(&canonical[j]) || canonical[j].starts_with(&canonical[i]) {
                return Err(format!(
                    "Los destinos {} y {} se solapan entre sí.",
                    dests[i].display(),
                    dests[j].display()
                ));
            }
        }
    }

    Ok(canonical)
}

fn run_preflight(source: &Path, dests: &[PathBuf], opts: CopyOpts) -> Result<Vec<DestinationPlan>, String> {
    if !source.is_dir() {
        return Err("El origen debe ser una carpeta.".into());
    }
    if dests.is_empty() {
        return Err("Agrega al menos un destino.".into());
    }

    let canonical_dests = validate_destinations(source, dests)?;
    let files = list_source_files(source)?;
    if files.is_empty() {
        return Err("El origen no tiene archivos.".into());
    }

    canonical_dests
        .iter()
        .map(|dest| plan_destination(source, dest, &files, opts))
        .collect()
}

pub fn start_job(
    source: PathBuf,
    dests: Vec<PathBuf>,
    opts: CopyOpts,
) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    let _plans = run_preflight(&source, &dests, opts)?;
    engine_impl::start_job(source, dests, opts)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let p = std::env::temp_dir().join(format!("disk-duplicator-preflight-{name}-{stamp}"));
        fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn round_up_tracks_allocation_units() {
        assert_eq!(round_up(0, 4096), 0);
        assert_eq!(round_up(1, 4096), 4096);
        assert_eq!(round_up(4096, 4096), 4096);
        assert_eq!(round_up(4097, 4096), 8192);
    }

    #[test]
    fn rejects_duplicate_destinations() {
        let root = temp_dir("duplicates");
        let src = root.join("src");
        let dst = root.join("dst");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(src.join("a.bin"), b"x").unwrap();
        let result = validate_destinations(&src, &[dst.clone(), dst.clone()]);
        assert!(result.is_err());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn rejects_overlapping_destination_roots() {
        let root = temp_dir("overlap");
        let src = root.join("src");
        let a = root.join("a");
        let b = a.join("b");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&b).unwrap();
        fs::write(src.join("a.bin"), b"x").unwrap();
        let result = validate_destinations(&src, &[a, b]);
        assert!(result.is_err());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn cleans_only_owned_part_files() {
        let root = temp_dir("parts");
        let src = root.join("src");
        let dst = root.join("dst");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(src.join("a.bin"), b"abc").unwrap();
        let files = list_source_files(&src).unwrap();
        let owned = part_path(&dst.join("a.bin"));
        let unrelated = dst.join("unrelated.part");
        fs::write(&owned, b"partial").unwrap();
        fs::write(&unrelated, b"keep").unwrap();
        cleanup_owned_stale_parts(&dst, &files).unwrap();
        assert!(!owned.exists());
        assert!(unrelated.exists());
        let _ = fs::remove_dir_all(root);
    }
}
