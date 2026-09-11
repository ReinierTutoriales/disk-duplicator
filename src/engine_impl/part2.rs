fn fast_state_key(info: &FileInfo) -> String {
    format!("{}|{}|{}", persisted_path_key(&info.rel), info.size, info.mtime_ns)
}

fn hash_file_with_buffer(
    path: &Path,
    buf: &mut [u8],
    state: Option<&JobState>,
) -> Result<blake3::Hash, String> {
    let mut f = File::open(path).map_err(|e| format!("verificar: {e}"))?;
    let mut h = blake3::Hasher::new();
    loop {
        if let Some(s) = state {
            if !wait_pause(s) { return Err("Cancelado".into()); }
        }
        let n = f.read(buf).map_err(|e| format!("verificar: {e}"))?;
        if n == 0 { break; }
        h.update(&buf[..n]);
    }
    Ok(h.finalize())
}

fn validate_part_size(part: &Path, expected: u64) -> Result<(), String> {
    let actual = fs::metadata(part)
        .map_err(|e| format!("No se pudo inspeccionar {} antes del commit: {e}", part.display()))?
        .len();
    if actual != expected {
        return Err(format!(
            "Tamaño físico incorrecto en {}: esperado {}, obtenido {}.",
            part.display(), expected, actual
        ));
    }
    Ok(())
}

fn commit_part_fast(dest_root: &Path, part: &Path, dst: &Path) -> Result<(), String> {
    if !dst.exists() {
        return fs::rename(part, dst).map_err(|e| format!("rename {}: {e}", dst.display()));
    }

    let backup = backup_path(dest_root, dst);
    if backup.exists() {
        fs::remove_file(&backup)
            .map_err(|e| format!("No se pudo limpiar backup {}: {e}", backup.display()))?;
    }
    fs::rename(dst, &backup)
        .map_err(|e| format!("No se pudo preparar reemplazo {}: {e}", dst.display()))?;
    match fs::rename(part, dst) {
        Ok(()) => {
            if let Err(e) = fs::remove_file(&backup) {
                eprintln!("Advertencia: no se pudo eliminar backup {} después del commit: {e}", backup.display());
            }
            Ok(())
        }
        Err(commit_err) => match fs::rename(&backup, dst) {
            Ok(()) => Err(format!(
                "No se pudo reemplazar {}: {commit_err}. El archivo original fue restaurado correctamente.",
                dst.display()
            )),
            Err(restore_err) => Err(format!(
                "CRÍTICO: falló el reemplazo de {} ({commit_err}) y también falló restaurar el archivo original desde {} ({restore_err}). El backup original permanece en {}.",
                dst.display(),
                backup.display(),
                backup.display()
            )),
        },
    }
}

fn retry_commit(
    state: &JobState,
    slot: usize,
    mut op: impl FnMut() -> Result<(), String>,
) -> Result<(), String> {
    let mut last_err = String::new();
    for attempt in 0..=RETRIES {
        match op() {
            Ok(()) => return Ok(()),
            Err(e) => {
                if e.starts_with("CRÍTICO:") {
                    return Err(e);
                }
                last_err = e;
                if attempt < RETRIES {
                    state.dests.lock().unwrap()[slot].retries += 1;
                    thread::sleep(Duration::from_millis(75 * (attempt as u64 + 1)));
                    if !wait_pause(state) { return Err("Cancelado".into()); }
                }
            }
        }
    }
    Err(last_err)
}

fn reset_part_after_partial_write(
    file: &mut Option<File>,
    tmp: &Path,
    committed: u64,
) -> Result<(), String> {
    file.take();
    let reset = OpenOptions::new()
        .write(true)
        .open(tmp)
        .map_err(|e| format!("No se pudo reabrir temporal {} para recuperar escritura parcial: {e}", tmp.display()))?;
    reset
        .set_len(committed)
        .map_err(|e| format!("No se pudo truncar temporal {} tras escritura parcial: {e}", tmp.display()))?;
    *file = Some(
        OpenOptions::new()
            .append(true)
            .open(tmp)
            .map_err(|e| format!("No se pudo reabrir temporal {} tras recuperar escritura parcial: {e}", tmp.display()))?,
    );
    Ok(())
}

struct WriteRetryContext<'a> {
    state: &'a JobState,
    slot: usize,
    control: &'a DestControl,
    rel: &'a str,
}

fn write_buffer_retrying(
    file: &mut Option<File>,
    tmp: &Path,
    committed: u64,
    data: &[u8],
    ctx: &WriteRetryContext<'_>,
) -> Result<(), String> {
    const WRITE_CHUNK: usize = 4 * 1024 * 1024;
    let mut last_err = String::new();

    for attempt in 0..=RETRIES {
        if file.is_none() {
            return Err("archivo temporal no disponible".to_owned());
        }

        let mut offset = 0usize;
        let mut attempt_error = None;

        #[cfg(windows)]
        let mut native_writer = match crate::windows_io::CancelableFile::reopen_at(tmp, committed) {
            Ok(writer) => Some(writer),
            Err(e) => {
                attempt_error = Some(format!("abrir I/O nativo {}: {e}", ctx.rel));
                None
            }
        };

        while offset < data.len() && attempt_error.is_none() {
            if !wait_pause(ctx.state) {
                attempt_error = Some("Cancelado".to_owned());
                break;
            }
            let end = (offset + WRITE_CHUNK).min(data.len());

            #[cfg(windows)]
            let write_result = native_writer
                .as_mut()
                .expect("native writer initialized")
                .write_all_cancelable(&data[offset..end], || {
                    ctx.state.cancel.load(Ordering::Acquire)
                        || !ctx.control.alive.load(Ordering::Acquire)
                });

            #[cfg(not(windows))]
            let write_result = file
                .as_mut()
                .expect("temporary file initialized")
                .write_all(&data[offset..end]);

            match write_result {
                Ok(()) => {
                    offset = end;
                    ctx.control.note_progress();
                }
                Err(e) => {
                    if e.kind() == std::io::ErrorKind::Interrupted
                        && ctx.state.cancel.load(Ordering::Acquire)
                    {
                        attempt_error = Some("Cancelado".to_owned());
                    } else {
                        attempt_error = Some(format!("escritura {}: {e}", ctx.rel));
                    }
                }
            }
        }

        #[cfg(windows)]
        drop(native_writer.take());

        if attempt_error.is_none() {
            return Ok(());
        }
        last_err = attempt_error.expect("write attempt error");
        if last_err == "Cancelado" {
            return Err(last_err);
        }

        reset_part_after_partial_write(file, tmp, committed)?;
        if attempt < RETRIES {
            ctx.state.dests.lock().unwrap()[ctx.slot].retries += 1;
            thread::sleep(Duration::from_millis(75 * (attempt as u64 + 1)));
            if !wait_pause(ctx.state) { return Err("Cancelado".into()); }
        }
    }

    Err(last_err)
}

fn sync_temp_file(
    file: &File,
    state: &JobState,
    control: &DestControl,
    rel: &str,
) -> Result<(), String> {
    #[cfg(windows)]
    {
        let clone = file
            .try_clone()
            .map_err(|e| format!("sync clone {rel}: {e}"))?;
        crate::windows_io::sync_file_cancelable(clone, || {
            state.cancel.load(Ordering::Acquire) || !control.alive.load(Ordering::Acquire)
        })
        .map_err(|e| {
            if e.kind() == std::io::ErrorKind::Interrupted
                && state.cancel.load(Ordering::Acquire)
            {
                "Cancelado".to_owned()
            } else {
                format!("sync {rel}: {e}")
            }
        })
    }

    #[cfg(not(windows))]
    {
        file.sync_data().map_err(|e| format!("sync {rel}: {e}"))
    }
}

fn fanout_worker(
    dest: PathBuf,
    rx: mpsc::Receiver<FanoutItem>,
    control: Arc<DestControl>,
    state: Arc<JobState>,
    slot: usize,
    opts: CopyOpts,
) {
    const VERIFY_BUFFER: usize = 8 * 1024 * 1024;

    let start = Instant::now();
    let mut effective_written = 0u64;
    let mut current: Option<CurrentFile> = None;

    let tmp_dir = state_dir_for(&dest).join("tmp");
    if let Err(e) = fs::create_dir_all(&tmp_dir) {
        control.alive.store(false, Ordering::Release);
        set_phase(
            &state,
            slot,
            DestPhase::Failed,
            Some(format!("tmp mkdir {}: {e}", tmp_dir.display())),
        );
        return;
    }

    let mut verify_buf = opts.verify.then(|| vec![0u8; VERIFY_BUFFER]);
    let mut journal = match StateJournal::open(&dest) {
        Ok(j) => j,
        Err(e) => {
            control.alive.store(false, Ordering::Release);
            set_phase(&state, slot, DestPhase::Failed, Some(e));
            return;
        }
    };
    let mut manifest = match ManifestWriter::open(&dest) {
        Ok(m) => m,
        Err(e) => {
            control.alive.store(false, Ordering::Release);
            set_phase(&state, slot, DestPhase::Failed, Some(e));
            return;
        }
    };

    set_phase(&state, slot, DestPhase::Copying, None);

    while let Ok(item) = rx.recv() {
        if !wait_pause(&state) { break; }
        if !control.alive.load(Ordering::Acquire) { break; }
        match item {
            FanoutItem::Begin(info) => {
                state.dests.lock().unwrap()[slot].last_file = info.rel.to_string_lossy().into_owned();
                let dst = dest.join(&info.rel);
                cleanup_part(&dest, &dst);
                let tmp = part_path(&dest, &dst);

                match File::create(&tmp) {
                    Ok(file) => current = Some(CurrentFile {
                        info,
                        file: Some(file),
                        hasher: blake3::Hasher::new(),
                        copied: 0,
                        failed: false,
                    }),
                    Err(e) => {
                        record_file_error(&state, slot, format!(".part {}: {e}", dst.display()));
                        current = Some(CurrentFile {
                            info,
                            file: None,
                            hasher: blake3::Hasher::new(),
                            copied: 0,
                            failed: true,
                        });
                        if !opts.keep_going {
                            control.alive.store(false, Ordering::Release);
                            break;
                        }
                    }
                }
            }
            FanoutItem::Data(buf) => {
                if let Some(cur) = current.as_mut() {
                    if !cur.failed {
                        let rel = cur.info.rel.display().to_string();
                        let dst = dest.join(&cur.info.rel);
                        let tmp = part_path(&dest, &dst);
                        let ctx = WriteRetryContext {
                            state: &state,
                            slot,
                            control: &control,
                            rel: &rel,
                        };
                        let bytes = buf.bytes();
                        let result = write_buffer_retrying(
                            &mut cur.file,
                            &tmp,
                            cur.copied,
                            bytes,
                            &ctx,
                        );
                        match result {
                            Ok(()) => {
                                if !opts.verify {
                                    cur.hasher.update(bytes);
                                }
                                let written = bytes.len() as u64;
                                cur.copied += written;
                                record_write_progress(
                                    &state,
                                    slot,
                                    written,
                                    &mut effective_written,
                                    start,
                                );
                            }
                            Err(e) => {
                                cur.failed = true;
                                cur.file.take();
                                cleanup_part(&dest, &dest.join(&cur.info.rel));
                                rollback_write_progress(
                                    &state,
                                    slot,
                                    cur.copied,
                                    &mut effective_written,
                                    start,
                                );
                                if e != "Cancelado" {
                                    record_file_error(&state, slot, e);
                                }
                                if !opts.keep_going || state.cancel.load(Ordering::Acquire) {
                                    control.alive.store(false, Ordering::Release);
                                }
                            }
                        }
                    }
                }
                control.queue_depth.fetch_sub(1, Ordering::AcqRel);
                state.dests.lock().unwrap()[slot].queue_depth =
                    control.queue_depth.load(Ordering::Acquire);
                if !control.alive.load(Ordering::Acquire) { break; }
            }
            FanoutItem::End { hash } => {
                let Some(mut cur) = current.take() else { continue; };
                let dst = dest.join(&cur.info.rel);
                let tmp = part_path(&dest, &dst);

                if !control.alive.load(Ordering::Acquire) {
                    cur.file.take();
                    cleanup_part(&dest, &dst);
                    continue;
                }

                if cur.failed {
                    cleanup_part(&dest, &dst);
                    if !opts.keep_going {
                        control.alive.store(false, Ordering::Release);
                        break;
                    }
                    continue;
                }

                if cur.copied != cur.info.size {
                    cur.file.take();
                    cleanup_part(&dest, &dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    record_file_error(
                        &state,
                        slot,
                        format!("tamaño inesperado en {}", cur.info.rel.display()),
                    );
                    if !opts.keep_going {
                        control.alive.store(false, Ordering::Release);
                        break;
                    }
                    continue;
                }

                if !wait_pause(&state) {
                    cur.file.take();
                    cleanup_part(&dest, &dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    break;
                }

                if let Some(file) = cur.file.as_ref() {
                    let rel = cur.info.rel.display().to_string();
                    control.enter_operation(OperationPhase::Sync);
                    let sync_result = retry_io(&state, slot, || {
                        sync_temp_file(file, &state, &control, &rel)
                    });
                    control.enter_operation(OperationPhase::Write);
                    if let Err(e) = sync_result {
                        cur.file.take();
                        cleanup_part(&dest, &dst);
                        rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                        if e != "Cancelado" {
                            record_file_error(&state, slot, e);
                        }
                        if !opts.keep_going || state.cancel.load(Ordering::Acquire) {
                            control.alive.store(false, Ordering::Release);
                            break;
                        }
                        continue;
                    }
                }
                cur.file.take();

                if !control.alive.load(Ordering::Acquire) {
                    cleanup_part(&dest, &dst);
                    continue;
                }

                if let Err(e) = validate_part_size(&tmp, cur.info.size) {
                    cleanup_part(&dest, &dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    record_file_error(&state, slot, e);
                    if !opts.keep_going {
                        control.alive.store(false, Ordering::Release);
                        break;
                    }
                    continue;
                }

                let expected = if opts.verify {
                    blake3::Hash::from_bytes(hash)
                } else {
                    let streamed = cur.hasher.finalize();
                    if streamed.as_bytes() != &hash {
                        cleanup_part(&dest, &dst);
                        rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                        record_file_error(
                            &state,
                            slot,
                            format!("hash de flujo no coincide: {}", dst.display()),
                        );
                        if !opts.keep_going {
                            control.alive.store(false, Ordering::Release);
                            break;
                        }
                        continue;
                    }
                    streamed
                };

                if !wait_pause(&state) {
                    cleanup_part(&dest, &dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    break;
                }

                if let Some(buf) = verify_buf.as_mut() {
                    set_phase(&state, slot, DestPhase::Verifying, None);
                    control.enter_operation(OperationPhase::Verify);
                    let verify_result = hash_file_with_buffer(&tmp, buf, Some(&state));
                    control.enter_operation(OperationPhase::Write);
                    match verify_result {
                        Ok(actual) if actual == expected => {}
                        Ok(_) => {
                            cleanup_part(&dest, &dst);
                            rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                            record_file_error(
                                &state,
                                slot,
                                format!("BLAKE3 no coincide: {}", dst.display()),
                            );
                            set_phase(&state, slot, DestPhase::Copying, None);
                            if !opts.keep_going {
                                control.alive.store(false, Ordering::Release);
                                break;
                            }
                            continue;
                        }
                        Err(e) => {
                            cleanup_part(&dest, &dst);
                            rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                            if e == "Cancelado" {
                                control.alive.store(false, Ordering::Release);
                                break;
                            }
                            record_file_error(&state, slot, e);
                            set_phase(&state, slot, DestPhase::Copying, None);
                            if !opts.keep_going {
                                control.alive.store(false, Ordering::Release);
                                break;
                            }
                            continue;
                        }
                    }
                    set_phase(&state, slot, DestPhase::Copying, None);
                }

                if !control.alive.load(Ordering::Acquire) {
                    cleanup_part(&dest, &dst);
                    continue;
                }

                if !wait_pause(&state) {
                    cleanup_part(&dest, &dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    break;
                }

                control.enter_operation(OperationPhase::Commit);
                let commit_result = retry_commit(&state, slot, || commit_part_fast(&dest, &tmp, &dst));
                control.enter_operation(OperationPhase::Write);
                if let Err(e) = commit_result {
                    cleanup_part(&dest, &dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    if e != "Cancelado" {
                        record_file_error(&state, slot, e);
                    }
                    if !opts.keep_going || state.cancel.load(Ordering::Acquire) {
                        control.alive.store(false, Ordering::Release);
                        break;
                    }
                    continue;
                }

                if !control.alive.load(Ordering::Acquire) {
                    continue;
                }

                if let Some(m) = expected_mtime(&cur.info) {
                    if let Err(e) = set_mtime(&dst, m) {
                        record_file_error(&state, slot, e);
                        if !opts.keep_going {
                            control.alive.store(false, Ordering::Release);
                            break;
                        }
                    }
                }

                if !control.alive.load(Ordering::Acquire) {
                    continue;
                }

                if let Err(e) = manifest.append(&cur.info.rel, &expected) {
                    record_file_error(&state, slot, e);
                    if !opts.keep_going {
                        control.alive.store(false, Ordering::Release);
                        break;
                    }
                    continue;
                }

                if let Err(e) = journal.append(&fast_state_key(&cur.info)) {
                    record_file_error(&state, slot, e);
                    if !opts.keep_going {
                        control.alive.store(false, Ordering::Release);
                        break;
                    }
                    continue;
                }

                if journal.needs_checkpoint() {
                    if let Err(e) = checkpoint_logs(&mut manifest, &mut journal) {
                        record_file_error(&state, slot, e);
                        if !opts.keep_going {
                            control.alive.store(false, Ordering::Release);
                            break;
                        }
                        continue;
                    }
                }

                control.note_progress();
                record_done(&state, slot);
            }
        }
    }

    if let Some(cur) = current.take() {
        cleanup_part(&dest, &dest.join(&cur.info.rel));
        if cur.copied > 0 && !cur.failed {
            rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
        }
    }

    if let Err(e) = finish_logs(&mut manifest, &mut journal) {
        set_phase(&state, slot, DestPhase::Failed, Some(e));
        control.alive.store(false, Ordering::Release);
        return;
    }

    if state.cancel.load(Ordering::Relaxed) {
        set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
        return;
    }

    let errs = state.dests.lock().unwrap()[slot].files_err;
    if !control.alive.load(Ordering::Acquire) {
        set_phase(&state, slot, DestPhase::Failed, None);
    } else if errs == 0 {
        set_phase(&state, slot, DestPhase::Done, None);
    } else {
        set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es).")));
    }
}