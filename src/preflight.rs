use crate::engine_impl::{self, CopyOpts, DestPhase, FileInfo, JobState};
use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use walkdir::WalkDir;

const MIN_FREE_RESERVE: u64 = 1024 * 1024 * 1024;
const MAX_FREE_RESERVE: u64 = 16 * 1024 * 1024 * 1024;
const RESERVE_PERCENT: u64 = 1;

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
    plans: Vec<DestinationPlan>,
}

fn metadata_mtime_ns(meta: &fs::Metadata) -> u128 {
    meta.modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_nanos())
        .unwrap_or(0)
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
        let meta = entry
            .metadata()
            .map_err(|e| format!("metadata {}: {e}", entry.path().display()))?;
        File::open(entry.path())
            .map_err(|e| format!("No se puede leer {}: {e}", entry.path().display()))?;
        let rel = entry
            .path()
            .strip_prefix(root)
            .map_err(|e| e.to_string())?
            .to_path_buf();
        files.push(PlannedFile {
            rel,
            size: meta.len(),
            mtime_ns: metadata_mtime_ns(&meta),
        });
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

fn state_path(dest: &Path) -> PathBuf {
    dest.join(".disk-duplicator").join("completed.jsonl")
}

fn load_completed(dest: &Path) -> HashSet<String> {
    let Ok(text) = fs::read_to_string(state_path(dest)) else {
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

fn rewrite_completed(dest: &Path, keys: &HashSet<String>) -> Result<(), String> {
    let dir = dest.join(".disk-duplicator");
    fs::create_dir_all(&dir).map_err(|e| format!("state mkdir {}: {e}", dir.display()))?;
    let path = state_path(dest);
    let tmp = dir.join("completed.jsonl.preflight");
    let result = (|| {
        let mut f = File::create(&tmp).map_err(|e| format!("state temp {}: {e}", tmp.display()))?;
        let mut ordered: Vec<&String> = keys.iter().collect();
        ordered.sort();
        for key in ordered {
            writeln!(f, "{{\"key\":\"{key}\"}}").map_err(|e| format!("state write: {e}"))?;
        }
        f.sync_all().map_err(|e| format!("state sync: {e}"))?;
        drop(f);
        if path.exists() {
            fs::remove_file(&path).map_err(|e| format!("state replace {}: {e}", path.display()))?;
        }
        fs::rename(&tmp, &path).map_err(|e| format!("state commit {}: {e}", path.display()))?;
        Ok::<(), String>(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result
}

fn same_enough(src: &Path, dst: &Path) -> bool {
    let Ok(a) = fs::metadata(src) else { return false; };
    let Ok(b) = fs::metadata(dst) else { return false; };
    if !a.is_file() || !b.is_file() || a.len() != b.len() {
        return false;
    }
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
    if loaded.is_empty() {
        return Ok(loaded);
    }

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
    let completed = normalize_completed_state(source, dest, files)?;

    let mut plan = DestinationPlan {
        available_space: available,
        ..DestinationPlan::default()
    };
    let mut committed_delta: i128 = 0;
    let mut peak_extra: i128 = 0;

    for info in files {
        validate_destination_layout(dest, &info.rel)?;
        let src = source.join(&info.rel);
        let dst = dest.join(&info.rel);
        let key = state_key(info);
        if completed.contains(&key) || (opts.skip_same && same_enough(&src, &dst)) {
            plan.skipped_files += 1;
            continue;
        }

        let old_alloc = destination_file_allocation(&dst, granularity)?;
        let new_alloc = round_up(info.size, granularity);

        if old_alloc.is_some() {
            plan.replace_files += 1;
        } else {
            plan.new_files += 1;
        }
        plan.bytes_to_write = plan.bytes_to_write.saturating_add(info.size);

        let during_temp = committed_delta.saturating_add(new_alloc as i128);
        peak_extra = peak_extra.max(during_temp);
        committed_delta = committed_delta
            .saturating_add(new_alloc as i128)
            .saturating_sub(old_alloc.unwrap_or(0) as i128);
    }

    plan.peak_extra_space = peak_extra.max(0).min(u64::MAX as i128) as u64;
    plan.reserve_space = if plan.bytes_to_write == 0 {
        0
    } else {
        reserve_for_volume(total)
    };

    let required_with_reserve = plan.peak_extra_space.saturating_add(plan.reserve_space);
    if available < required_with_reserve {
        let missing = required_with_reserve - available;
        return Err(format!(
            "Espacio insuficiente en {}. Pico requerido: {} bytes + reserva: {} bytes; disponible: {} bytes; faltan: {} bytes.",
            dest.display(),
            plan.peak_extra_space,
            plan.reserve_space,
            plan.available_space,
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
        let root_meta = fs::symlink_metadata(dest)
            .map_err(|e| format!("destino {}: {e}", dest.display()))?;
        if root_meta.file_type().is_symlink() {
            return Err(format!("No se permite usar un enlace simbólico como destino: {}.", dest.display()));
        }
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

fn run_preflight(source: &Path, dests: &[PathBuf], opts: CopyOpts) -> Result<PreflightResult, String> {
    if !source.is_dir() {
        return Err("El origen debe ser una carpeta.".into());
    }
    if dests.is_empty() {
        return Err("Agrega al menos un destino.".into());
    }

    let canonical_dests = validate_destinations(source, dests)?;
    let files = Arc::new(list_source_files(source)?);
    if files.is_empty() {
        return Err("El origen no tiene archivos.".into());
    }

    let plans = canonical_dests
        .iter()
        .map(|dest| plan_destination(source, dest, &files, opts))
        .collect::<Result<Vec<_>, _>>()?;

    Ok(PreflightResult { files, plans })
}

fn source_change(source: &Path, files: &[PlannedFile]) -> Option<String> {
    for info in files {
        let path = source.join(&info.rel);
        let Ok(meta) = fs::metadata(&path) else {
            return Some(format!("El archivo de origen desapareció durante la copia: {}", path.display()));
        };
        if !meta.is_file() || meta.len() != info.size || metadata_mtime_ns(&meta) != info.mtime_ns {
            return Some(format!("El archivo de origen cambió durante la copia: {}", path.display()));
        }
    }
    None
}

fn supervise_job(
    source: PathBuf,
    files: Arc<Vec<PlannedFile>>,
    state: Arc<JobState>,
    handles: Vec<JoinHandle<()>>,
) -> JoinHandle<()> {
    thread::spawn(move || {
        while handles.iter().any(|h| !h.is_finished()) {
            state.running.store(true, Ordering::Release);
            thread::sleep(Duration::from_millis(20));
        }

        let mut worker_panicked = false;
        for handle in handles {
            if handle.join().is_err() {
                worker_panicked = true;
            }
        }

        let source_problem = source_change(&source, &files);
        if worker_panicked || source_problem.is_some() {
            let mut dests = state.dests.lock().unwrap();
            let message = source_problem.unwrap_or_else(|| "Un worker terminó de forma inesperada.".into());
            for dest in dests.iter_mut() {
                if matches!(dest.phase, DestPhase::Done | DestPhase::Idle | DestPhase::Copying | DestPhase::Verifying) {
                    dest.phase = DestPhase::Failed;
                    dest.files_err = dest.files_err.saturating_add(1);
                    dest.error = Some(message.clone());
                }
            }
        }

        state.running.store(false, Ordering::Release);
    })
}

pub fn start_job(
    source: PathBuf,
    dests: Vec<PathBuf>,
    opts: CopyOpts,
) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    let preflight = run_preflight(&source, &dests, opts)?;
    let _planned_bytes: u64 = preflight.plans.iter().map(|p| p.bytes_to_write).sum();
    let (state, handles) = engine_impl::start_job_with_files(
        source.clone(),
        dests,
        Arc::clone(&preflight.files),
        opts,
    )?;
    let supervisor = supervise_job(source, preflight.files, Arc::clone(&state), handles);
    Ok((state, vec![supervisor]))
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

    fn opts(skip_same: bool) -> CopyOpts {
        CopyOpts { verify: false, skip_same, keep_going: true }
    }

    #[test]
    fn round_up_tracks_allocation_units() {
        assert_eq!(round_up(0, 4096), 0);
        assert_eq!(round_up(1, 4096), 4096);
        assert_eq!(round_up(4096, 4096), 4096);
        assert_eq!(round_up(4097, 4096), 8192);
    }

    #[test]
    fn reserve_is_bounded() {
        assert_eq!(reserve_for_volume(10 * 1024 * 1024 * 1024), MIN_FREE_RESERVE);
        assert_eq!(reserve_for_volume(100 * 1024 * 1024 * 1024 * 1024), MAX_FREE_RESERVE);
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
    fn rejects_parent_file_layout_conflict() {
        let root = temp_dir("parent-conflict");
        let dst = root.join("dst");
        fs::create_dir_all(&dst).unwrap();
        fs::write(dst.join("folder"), b"not-a-directory").unwrap();
        let result = validate_destination_layout(&dst, Path::new("folder/file.bin"));
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

    #[test]
    fn stale_completed_state_is_removed() {
        let root = temp_dir("state");
        let src = root.join("src");
        let dst = root.join("dst");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(src.join("a.bin"), b"abc").unwrap();
        let files = list_source_files(&src).unwrap();
        let key = state_key(&files[0]);
        let dir = dst.join(".disk-duplicator");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("completed.jsonl"), format!("{{\"key\":\"{key}\"}}\n")).unwrap();
        let valid = normalize_completed_state(&src, &dst, &files).unwrap();
        assert!(valid.is_empty());
        assert!(load_completed(&dst).is_empty());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn zero_byte_existing_file_is_replacement() {
        let root = temp_dir("zero");
        let src = root.join("src");
        let dst = root.join("dst");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(src.join("a.bin"), []).unwrap();
        fs::write(dst.join("a.bin"), []).unwrap();
        let files = list_source_files(&src).unwrap();
        let plan = plan_destination(&src, &dst, &files, opts(false)).unwrap();
        assert_eq!(plan.new_files, 0);
        assert_eq!(plan.replace_files, 1);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn rejects_file_directory_type_conflict() {
        let root = temp_dir("type-conflict");
        let src = root.join("src");
        let dst = root.join("dst");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(dst.join("a.bin")).unwrap();
        fs::write(src.join("a.bin"), b"abc").unwrap();
        let files = list_source_files(&src).unwrap();
        assert!(plan_destination(&src, &dst, &files, opts(false)).is_err());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn all_skipped_plan_needs_no_free_space_reserve() {
        let root = temp_dir("no-write");
        let src = root.join("src");
        let dst = root.join("dst");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(src.join("a.bin"), b"abc").unwrap();
        fs::copy(src.join("a.bin"), dst.join("a.bin")).unwrap();
        let m = fs::metadata(src.join("a.bin")).unwrap().modified().unwrap();
        OpenOptions::new().write(true).open(dst.join("a.bin")).unwrap().set_modified(m).unwrap();
        let files = list_source_files(&src).unwrap();
        let plan = plan_destination(&src, &dst, &files, opts(true)).unwrap();
        assert_eq!(plan.bytes_to_write, 0);
        assert_eq!(plan.reserve_space, 0);
        let _ = fs::remove_dir_all(root);
    }
}
