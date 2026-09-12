fn plan_destination(
    source: &Path,
    dest: &Path,
    files: &[PlannedFile],
    dirs: &[PathBuf],
    opts: CopyOpts,
    source_hashes: &mut SourceHashCache,
    verify_buf: &mut [u8],
) -> Result<HashSet<PathBuf>, String> {
    prepare_state_dir(dest)?;
    cleanup_owned_stale_files(dest, files)?;
    validate_destination_directories(dest, dirs)?;

    let available = fs2::available_space(dest)
        .map_err(|e| format!("No se pudo consultar espacio libre en {}: {e}", dest.display()))?;
    let total = fs2::total_space(dest)
        .map_err(|e| format!("No se pudo consultar capacidad de {}: {e}", dest.display()))?;
    let granularity = fs2::allocation_granularity(dest).unwrap_or(4096).max(1);
    let completed = normalize_completed_state(
        source,
        dest,
        files,
        source_hashes,
        verify_buf,
    )?;
    compact_manifest(dest, files)?;
    let mut verified_skips = HashSet::new();

    let mut bytes_to_write = 0u64;
    let mut committed_delta: i128 = 0;
    let mut peak_extra: i128 = 0;

    for info in files {
        validate_destination_layout(dest, &info.rel)?;
        let src = source.join(&info.rel);
        let dst = dest.join(&info.rel);
        let key = state_key(info);
        let physically_valid = same_enough(&src, &dst);
        if completed.contains(&key) && physically_valid {
            verified_skips.insert(info.rel.clone());
            continue;
        }

        if opts.skip_same && physically_valid {
            let source_hash = cached_source_hash(source, info, source_hashes, verify_buf)?;
            let dest_hash = hash_path_with_buffer(&dst, verify_buf)?;
            if dest_hash.as_bytes() == &source_hash {
                verified_skips.insert(info.rel.clone());
                continue;
            }
        }

        let old_alloc = destination_file_allocation(&dst, granularity)?;
        let new_alloc = round_up(info.size, granularity);
        bytes_to_write = bytes_to_write.saturating_add(info.size);

        let during_temp = committed_delta.saturating_add(new_alloc as i128);
        peak_extra = peak_extra.max(during_temp);
        committed_delta = committed_delta
            .saturating_add(new_alloc as i128)
            .saturating_sub(old_alloc.unwrap_or(0) as i128);
    }

    let peak_extra_space = peak_extra.max(0).min(u64::MAX as i128) as u64;
    let reserve_space = if bytes_to_write == 0 {
        0
    } else {
        reserve_for_volume(total)
    };
    let required_with_reserve = peak_extra_space.saturating_add(reserve_space);
    if available < required_with_reserve {
        let missing = required_with_reserve - available;
        return Err(format!(
            "Espacio insuficiente en {}. Pico requerido: {} bytes + reserva: {} bytes; disponible: {} bytes; faltan: {} bytes.",
            dest.display(),
            peak_extra_space,
            reserve_space,
            available,
            missing
        ));
    }

    Ok(verified_skips)
}

fn canonical_existing(path: &Path, label: &str) -> Result<PathBuf, String> {
    path.canonicalize()
        .map_err(|e| format!("No se pudo resolver {label} {}: {e}", path.display()))
}

fn reject_reparse_root(path: &Path, label: &str) -> Result<(), String> {
    let meta = fs::symlink_metadata(path)
        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;
    if meta.file_type().is_symlink() || is_reparse_point(&meta) {
        return Err(format!(
            "No se permite usar un enlace simbólico, junction o reparse point como {label}: {}.",
            path.display()
        ));
    }
    Ok(())
}

fn validate_destinations(source: &Path, dests: &[PathBuf]) -> Result<Vec<PathBuf>, String> {
    let source_canon = canonical_existing(source, "origen")?;
    let mut canonical = Vec::with_capacity(dests.len());

    for dest in dests {
        fs::create_dir_all(dest).map_err(|e| format!("destino {}: {e}", dest.display()))?;
        reject_reparse_root(dest, "destino")?;
        let d = canonical_existing(dest, "destino")?;
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

struct SourcePlan {
    engine_root: PathBuf,
    overlap_path: PathBuf,
    files: Vec<PlannedFile>,
    dirs: Vec<PathBuf>,
    single_file: bool,
}

fn plan_source(source: &Path) -> Result<SourcePlan, String> {
    let meta = fs::symlink_metadata(source)
        .map_err(|e| format!("No se pudo inspeccionar el origen {}: {e}", source.display()))?;
    if meta.file_type().is_symlink() || is_reparse_point(&meta) {
        return Err(format!(
            "No se permite usar un enlace simbólico, junction o reparse point como origen: {}.",
            source.display()
        ));
    }

    let canonical = canonical_existing(source, "origen")?;
    if meta.is_dir() {
        let (files, dirs) = scan_source(&canonical)?;
        return Ok(SourcePlan {
            engine_root: canonical.clone(),
            overlap_path: canonical,
            files,
            dirs,
            single_file: false,
        });
    }

    if !meta.is_file() {
        return Err("El origen debe ser un archivo regular o una carpeta.".to_owned());
    }

    let file_name = canonical
        .file_name()
        .ok_or_else(|| "El archivo de origen no tiene un nombre válido.".to_owned())?
        .to_os_string();
    let parent = canonical
        .parent()
        .ok_or_else(|| "El archivo de origen no tiene una carpeta contenedora válida.".to_owned())?
        .to_path_buf();
    let file = File::open(&canonical)
        .map_err(|e| format!("No se puede leer {}: {e}", canonical.display()))?;
    let file_meta = file
        .metadata()
        .map_err(|e| format!("metadata {}: {e}", canonical.display()))?;
    if !file_meta.is_file() {
        return Err("El origen dejó de ser un archivo regular durante el análisis.".to_owned());
    }

    Ok(SourcePlan {
        engine_root: parent,
        overlap_path: canonical,
        files: vec![PlannedFile {
            rel: PathBuf::from(file_name),
            size: file_meta.len(),
            mtime_ns: metadata_mtime_ns(&file_meta),
        }],
        dirs: Vec::new(),
        single_file: true,
    })
}

fn run_preflight(
    source: &Path,
    dests: &[PathBuf],
    opts: CopyOpts,
) -> Result<PreflightPlan, String> {
    if dests.is_empty() {
        return Err("Agrega al menos un destino.".into());
    }

    let source_plan = plan_source(source)?;
    let canonical_dests = validate_destinations(&source_plan.overlap_path, dests)?;
    let canonical_source = source_plan.engine_root;
    let files = Arc::new(source_plan.files);
    let dirs = Arc::new(source_plan.dirs);
    let single_file = source_plan.single_file;
    let mut verified_skips = Vec::with_capacity(canonical_dests.len());
    let mut source_hashes = SourceHashCache::with_capacity(files.len());
    let mut verify_buf = vec![0u8; VERIFY_BUF];

    for dest in &canonical_dests {
        verified_skips.push(plan_destination(
            &canonical_source,
            dest,
            &files,
            &dirs,
            opts,
            &mut source_hashes,
            &mut verify_buf,
        )?);
    }

    Ok((
        PreflightResult {
            source: canonical_source,
            dests: canonical_dests,
            files,
            dirs,
            single_file,
        },
        Arc::new(verified_skips),
    ))
}

fn source_change(
    source: &Path,
    files: &[PlannedFile],
    dirs: &[PathBuf],
    single_file: bool,
) -> Option<String> {
    for info in files {
        let path = source.join(&info.rel);
        let Ok(meta) = fs::symlink_metadata(&path) else {
            return Some(format!(
                "El archivo de origen desapareció durante la copia: {}",
                path.display()
            ));
        };
        if meta.file_type().is_symlink()
            || is_reparse_point(&meta)
            || !meta.is_file()
            || meta.len() != info.size
            || metadata_mtime_ns(&meta) != info.mtime_ns
        {
            return Some(format!(
                "El archivo de origen cambió durante la copia: {}",
                path.display()
            ));
        }
    }
    for rel in dirs {
        let path = source.join(rel);
        let Ok(meta) = fs::symlink_metadata(&path) else {
            return Some(format!(
                "La carpeta de origen cambió durante la copia: {}",
                path.display()
            ));
        };
        if !meta.is_dir() || meta.file_type().is_symlink() || is_reparse_point(&meta) {
            return Some(format!(
                "La carpeta de origen cambió durante la copia: {}",
                path.display()
            ));
        }
    }

    if single_file {
        return None;
    }

    match scan_source(source) {
        Ok((now_files, now_dirs)) => {
            if now_files.len() != files.len() || now_dirs != dirs {
                return Some("La estructura del origen cambió durante la copia.".into());
            }
        }
        Err(e) => return Some(e),
    }
    None
}

fn hash_path_with_buffer(path: &Path, buf: &mut [u8]) -> Result<blake3::Hash, String> {
    hash_path_with_buffer_for_job(path, buf, None)
}

fn hash_path_with_buffer_for_job(
    path: &Path,
    buf: &mut [u8],
    state: Option<&JobState>,
) -> Result<blake3::Hash, String> {
    #[cfg(windows)]
    {
        crate::windows_io::hash_file_cancelable(path, buf.len(), || {
            state.is_some_and(|s| s.cancel.load(Ordering::Acquire))
        })
        .map_err(|e| {
            if e.kind() == std::io::ErrorKind::Interrupted {
                "Cancelado".to_owned()
            } else {
                format!("No se pudo verificar {}: {e}", path.display())
            }
        })
    }

    #[cfg(not(windows))]
    {
        let mut f =
            File::open(path).map_err(|e| format!("No se pudo verificar {}: {e}", path.display()))?;
        let mut h = blake3::Hasher::new();
        loop {
            if state.is_some_and(|s| s.cancel.load(Ordering::Acquire)) {
                return Err("Cancelado".to_owned());
            }
            let n = f
                .read(buf)
                .map_err(|e| format!("No se pudo verificar {}: {e}", path.display()))?;
            if n == 0 {
                break;
            }
            h.update(&buf[..n]);
        }
        Ok(h.finalize())
    }
}

#[cfg(test)]
fn final_source_hashes(
    source: &Path,
    files: &[PlannedFile],
    reader_hashes: &std::collections::HashMap<PathBuf, [u8; 32]>,
) -> Result<std::collections::HashMap<PathBuf, [u8; 32]>, String> {
    final_source_hashes_for_job(source, files, reader_hashes, None)
}

fn final_source_hashes_for_job(
    source: &Path,
    files: &[PlannedFile],
    reader_hashes: &std::collections::HashMap<PathBuf, [u8; 32]>,
    state: Option<&JobState>,
) -> Result<std::collections::HashMap<PathBuf, [u8; 32]>, String> {
    let mut final_hashes = std::collections::HashMap::with_capacity(files.len());
    let mut buf = vec![0u8; VERIFY_BUF];
    for info in files {
        if state.is_some_and(|s| s.cancel.load(Ordering::Acquire)) {
            return Err("Cancelado".to_owned());
        }
        let path = source.join(&info.rel);
        let actual = hash_path_with_buffer_for_job(&path, &mut buf, state)?;
        if let Some(read_hash) = reader_hashes.get(&info.rel) {
            if actual.as_bytes() != read_hash {
                return Err(format!(
                    "El archivo de origen cambió durante la copia aunque conservara tamaño y fecha: {}",
                    path.display()
                ));
            }
        }
        final_hashes.insert(info.rel.clone(), *actual.as_bytes());
    }
    Ok(final_hashes)
}

fn from_hex32(s: &str) -> Option<[u8; 32]> {
    let bytes = s.as_bytes();
    if bytes.len() != 64 {
        return None;
    }
    let mut out = [0u8; 32];
    for i in 0..32 {
        let hi = (bytes[i * 2] as char).to_digit(16)? as u8;
        let lo = (bytes[i * 2 + 1] as char).to_digit(16)? as u8;
        out[i] = (hi << 4) | lo;
    }
    Some(out)
}

fn manifest_key(path: &Path) -> String {
    persisted_path_key(path)
}

fn legacy_manifest_key(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

fn load_manifest_hashes(dest: &Path) -> std::collections::HashMap<String, [u8; 32]> {
    let path = manifest_path(dest);
    let Ok(text) = fs::read_to_string(&path) else {
        return std::collections::HashMap::new();
    };
    let mut hashes = std::collections::HashMap::new();
    for (index, line) in text.lines().enumerate() {
        let Some((hex, name)) = line.split_once("  ") else {
            eprintln!(
                "Advertencia: línea {} inválida en {}. La entrada no se usará para reanudación.",
                index + 1,
                path.display()
            );
            continue;
        };
        let Some(hash) = from_hex32(hex) else {
            eprintln!(
                "Advertencia: hash inválido en la línea {} de {}. La entrada no se usará para reanudación.",
                index + 1,
                path.display()
            );
            continue;
        };
        hashes.insert(name.to_owned(), hash);
    }
    hashes
}

fn compact_manifest(dest: &Path, files: &[PlannedFile]) -> Result<(), String> {
    let path = manifest_path(dest);
    if !path.exists() {
        return Ok(());
    }

    let hashes = load_manifest_hashes(dest);
    let mut entries = Vec::new();
    for info in files {
        let key = manifest_key(&info.rel);
        let old_key = legacy_manifest_key(&info.rel);
        if let Some(hash) = hashes.get(&key).or_else(|| hashes.get(&old_key)) {
            entries.push((key, *hash));
        }
    }
    entries.sort_by(|a, b| a.0.cmp(&b.0));

    let tmp = path.with_extension("b3.compact");
    let backup = path.with_extension("b3.compact.bak");
    if tmp.exists() {
        fs::remove_file(&tmp).map_err(|e| format!("No se pudo limpiar {}: {e}", tmp.display()))?;
    }
    if backup.exists() {
        fs::remove_file(&backup)
            .map_err(|e| format!("No se pudo limpiar {}: {e}", backup.display()))?;
    }

    let mut f =
        File::create(&tmp).map_err(|e| format!("No se pudo crear {}: {e}", tmp.display()))?;
    for (name, hash) in entries {
        let hex = blake3::Hash::from_bytes(hash).to_hex();
        writeln!(f, "{hex}  {name}")
            .map_err(|e| format!("No se pudo compactar manifest: {e}"))?;
    }
    f.sync_data()
        .map_err(|e| format!("No se pudo sincronizar manifest: {e}"))?;
    drop(f);

    fs::rename(&path, &backup)
        .map_err(|e| format!("No se pudo preparar compactación de {}: {e}", path.display()))?;
    match fs::rename(&tmp, &path) {
        Ok(()) => {
            fs::remove_file(&backup)
                .map_err(|e| format!("No se pudo limpiar {}: {e}", backup.display()))?;
            Ok(())
        }
        Err(commit_err) => match fs::rename(&backup, &path) {
            Ok(()) => Err(format!(
                "No se pudo compactar {}: {commit_err}; manifest anterior restaurado.",
                path.display()
            )),
            Err(restore_err) => Err(format!(
                "CRÍTICO: falló la compactación de {} ({commit_err}) y la restauración desde {} ({restore_err}).",
                path.display(), backup.display()
            )),
        },
    }
}

fn validate_destination_result_with_hashes(
    source: &Path,
    dest: &Path,
    files: &[PlannedFile],
    dirs: &[PathBuf],
    verify: bool,
    expected_hashes: &std::collections::HashMap<PathBuf, [u8; 32]>,
    state: Option<&JobState>,
) -> Result<(), String> {
    for rel in dirs {
        if state.is_some_and(|s| s.cancel.load(Ordering::Acquire)) {
            return Err("Cancelado".to_owned());
        }
        let path = dest.join(rel);
        let meta = fs::symlink_metadata(&path)
            .map_err(|_| format!("Falta la carpeta {}", path.display()))?;
        if !meta.is_dir() || meta.file_type().is_symlink() || is_reparse_point(&meta) {
            return Err(format!("La carpeta no coincide: {}", path.display()));
        }
    }

    let manifest_hashes = if verify {
        load_manifest_hashes(dest)
    } else {
        std::collections::HashMap::new()
    };
    let mut verify_buf = verify.then(|| vec![0u8; VERIFY_BUF]);

    let mut total_bytes = 0u64;
    for info in files {
        if state.is_some_and(|s| s.cancel.load(Ordering::Acquire)) {
            return Err("Cancelado".to_owned());
        }
        let dst = dest.join(&info.rel);
        let meta = fs::symlink_metadata(&dst)
            .map_err(|_| format!("Falta el archivo {}", dst.display()))?;
        if meta.file_type().is_symlink() || is_reparse_point(&meta) || !meta.is_file() {
            return Err(format!("La entrada no es un archivo normal: {}", dst.display()));
        }
        if meta.len() != info.size {
            return Err(format!(
                "Tamaño incorrecto en {}: esperado {}, obtenido {}.",
                dst.display(),
                info.size,
                meta.len()
            ));
        }
        total_bytes = total_bytes.saturating_add(meta.len());

        if verify {
            let buf = verify_buf.as_mut().expect("verify buffer");
            let dst_hash = hash_path_with_buffer_for_job(&dst, buf, state)?;
            if let Some(expected) = expected_hashes.get(&info.rel) {
                if dst_hash.as_bytes() != expected {
                    return Err(format!("BLAKE3 final no coincide: {}", dst.display()));
                }
            } else {
                let manifest_expected = manifest_hashes
                    .get(&manifest_key(&info.rel))
                    .or_else(|| manifest_hashes.get(&legacy_manifest_key(&info.rel)));
                if let Some(expected) = manifest_expected {
                    if dst_hash.as_bytes() != expected {
                        return Err(format!("BLAKE3 final no coincide: {}", dst.display()));
                    }
                } else {
                    let src = source.join(&info.rel);
                    if hash_path_with_buffer_for_job(&src, buf, state)? != dst_hash {
                        return Err(format!("BLAKE3 final no coincide: {}", dst.display()));
                    }
                }
            }
        }
    }

    let expected_bytes: u64 = files.iter().map(|f| f.size).sum();
    if total_bytes != expected_bytes {
        return Err(format!(
            "Validación de tamaño total falló en {}: esperado {}, obtenido {}.",
            dest.display(),
            expected_bytes,
            total_bytes
        ));
    }

    Ok(())
}

#[cfg(test)]
fn validate_destination_result(
    source: &Path,
    dest: &Path,
    files: &[PlannedFile],
    dirs: &[PathBuf],
    verify: bool,
) -> Result<(), String> {
    validate_destination_result_with_hashes(
        source,
        dest,
        files,
        dirs,
        verify,
        &std::collections::HashMap::new(),
        None,
    )
}
