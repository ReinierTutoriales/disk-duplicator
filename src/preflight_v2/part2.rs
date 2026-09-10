fn plan_destination(
    source: &Path,
    dest: &Path,
    files: &[PlannedFile],
    dirs: &[PathBuf],
    opts: CopyOpts,
) -> Result<(), String> {
    cleanup_owned_stale_files(dest, files)?;
    validate_destination_directories(dest, dirs)?;

    let available = fs2::available_space(dest)
        .map_err(|e| format!("No se pudo consultar espacio libre en {}: {e}", dest.display()))?;
    let total = fs2::total_space(dest)
        .map_err(|e| format!("No se pudo consultar capacidad de {}: {e}", dest.display()))?;
    let granularity = fs2::allocation_granularity(dest).unwrap_or(4096).max(1);
    let completed = normalize_completed_state(source, dest, files)?;

    let mut bytes_to_write = 0u64;
    let mut committed_delta: i128 = 0;
    let mut peak_extra: i128 = 0;

    for info in files {
        validate_destination_layout(dest, &info.rel)?;
        let src = source.join(&info.rel);
        let dst = dest.join(&info.rel);
        let key = state_key(info);
        let physically_valid = same_enough(&src, &dst);
        if (completed.contains(&key) && physically_valid) || (opts.skip_same && physically_valid) {
            continue;
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
    let reserve_space = if bytes_to_write == 0 { 0 } else { reserve_for_volume(total) };
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

    Ok(())
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
    if !source.is_dir() { return Err("El origen debe ser una carpeta.".into()); }
    if dests.is_empty() { return Err("Agrega al menos un destino.".into()); }

    let canonical_source = canonical_existing(source, "origen")?;
    let canonical_dests = validate_destinations(&canonical_source, dests)?;
    let (files, dirs) = scan_source(&canonical_source)?;
    let files = Arc::new(files);
    let dirs = Arc::new(dirs);

    for dest in &canonical_dests {
        plan_destination(&canonical_source, dest, &files, &dirs, opts)?;
    }

    Ok(PreflightResult {
        source: canonical_source,
        dests: canonical_dests,
        files,
        dirs,
    })
}

fn source_change(source: &Path, files: &[PlannedFile], dirs: &[PathBuf]) -> Option<String> {
    for info in files {
        let path = source.join(&info.rel);
        let Ok(meta) = fs::metadata(&path) else {
            return Some(format!("El archivo de origen desapareció durante la copia: {}", path.display()));
        };
        if !meta.is_file() || meta.len() != info.size || metadata_mtime_ns(&meta) != info.mtime_ns {
            return Some(format!("El archivo de origen cambió durante la copia: {}", path.display()));
        }
    }
    for rel in dirs {
        let path = source.join(rel);
        if !path.is_dir() {
            return Some(format!("La carpeta de origen cambió durante la copia: {}", path.display()));
        }
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

fn hash_path(path: &Path) -> Result<blake3::Hash, String> {
    let mut f = File::open(path).map_err(|e| format!("No se pudo verificar {}: {e}", path.display()))?;
    let mut buf = vec![0u8; VERIFY_BUF];
    let mut h = blake3::Hasher::new();
    loop {
        let n = f.read(&mut buf).map_err(|e| format!("No se pudo verificar {}: {e}", path.display()))?;
        if n == 0 { break; }
        h.update(&buf[..n]);
    }
    Ok(h.finalize())
}

fn from_hex32(s: &str) -> Option<[u8; 32]> {
    let bytes = s.as_bytes();
    if bytes.len() != 64 { return None; }
    let mut out = [0u8; 32];
    for i in 0..32 {
        let hi = (bytes[i * 2] as char).to_digit(16)? as u8;
        let lo = (bytes[i * 2 + 1] as char).to_digit(16)? as u8;
        out[i] = (hi << 4) | lo;
    }
    Some(out)
}

fn manifest_key(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

fn load_manifest_hashes(dest: &Path) -> std::collections::HashMap<String, [u8; 32]> {
    let path = state_dir_for(dest).join("manifest.b3");
    let Ok(text) = fs::read_to_string(path) else { return std::collections::HashMap::new(); };
    text.lines()
        .filter_map(|line| {
            let (hex, name) = line.split_once("  ")?;
            Some((name.to_owned(), from_hex32(hex)?))
        })
        .collect()
}

fn validate_destination_result_with_hashes(
    source: &Path,
    dest: &Path,
    files: &[PlannedFile],
    dirs: &[PathBuf],
    verify: bool,
    reader_hashes: &std::collections::HashMap<PathBuf, [u8; 32]>,
) -> Result<(), String> {
    for rel in dirs {
        let path = dest.join(rel);
        let meta = fs::symlink_metadata(&path)
            .map_err(|_| format!("Falta la carpeta {}", path.display()))?;
        if !meta.is_dir() || meta.file_type().is_symlink() {
            return Err(format!("La carpeta no coincide: {}", path.display()));
        }
    }

    let manifest_hashes = if verify {
        load_manifest_hashes(dest)
    } else {
        std::collections::HashMap::new()
    };

    let mut total_bytes = 0u64;
    for info in files {
        let dst = dest.join(&info.rel);
        let meta = fs::metadata(&dst)
            .map_err(|_| format!("Falta el archivo {}", dst.display()))?;
        if !meta.is_file() {
            return Err(format!("La entrada no es un archivo: {}", dst.display()));
        }
        if meta.len() != info.size {
            return Err(format!(
                "Tamaño incorrecto en {}: esperado {}, obtenido {}.",
                dst.display(), info.size, meta.len()
            ));
        }
        total_bytes = total_bytes.saturating_add(meta.len());

        if verify {
            let dst_hash = hash_path(&dst)?;
            if let Some(expected) = reader_hashes.get(&info.rel) {
                if dst_hash.as_bytes() != expected {
                    return Err(format!("BLAKE3 final no coincide: {}", dst.display()));
                }
            } else if let Some(expected) = manifest_hashes.get(&manifest_key(&info.rel)) {
                if dst_hash.as_bytes() != expected {
                    return Err(format!("BLAKE3 final no coincide: {}", dst.display()));
                }
            } else {
                let src = source.join(&info.rel);
                if hash_path(&src)? != dst_hash {
                    return Err(format!("BLAKE3 final no coincide: {}", dst.display()));
                }
            }
        }
    }

    let expected_bytes: u64 = files.iter().map(|f| f.size).sum();
    if total_bytes != expected_bytes {
        return Err(format!(
            "Validación de tamaño total falló en {}: esperado {}, obtenido {}.",
            dest.display(), expected_bytes, total_bytes
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
    )
}
