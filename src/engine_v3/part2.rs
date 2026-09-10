fn fast_state_key(info: &FileInfo) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let path = info.rel.to_string_lossy();
    let bytes = path.as_bytes();
    let mut out = String::with_capacity(bytes.len() * 2 + 48);
    for &b in bytes {
        out.push(HEX[(b >> 4) as usize] as char);
        out.push(HEX[(b & 0x0f) as usize] as char);
    }
    out.push('|');
    out.push_str(&info.size.to_string());
    out.push('|');
    out.push_str(&info.mtime_ns.to_string());
    out
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

fn fanout_worker(
    dest: PathBuf,
    rx: mpsc::Receiver<FanoutItem>,
    control: Arc<DestControl>,
    state: Arc<JobState>,
    slot: usize,
    opts: CopyOpts,
) {
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

    let mut verify_buf = vec![0u8; BLOCK];
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
        if state.cancel.load(Ordering::Relaxed) { break; }
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
                        let result = match cur.file.as_mut() {
                            Some(file) => retry_io(&state, slot, || {
                                file.write_all(&buf.data).map_err(|e| format!("escritura {rel}: {e}"))
                            }),
                            None => Err("archivo temporal no disponible".into()),
                        };
                        match result {
                            Ok(()) => {
                                cur.hasher.update(&buf.data);
                                cur.copied += buf.data.len() as u64;
                                control.note_progress();
                                record_write_progress(
                                    &state,
                                    slot,
                                    buf.data.len() as u64,
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
                                record_file_error(&state, slot, e);
                                if !opts.keep_going {
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

                if let Some(file) = cur.file.as_mut() {
                    let rel = cur.info.rel.display().to_string();
                    if let Err(e) = retry_io(&state, slot, || {
                        file.sync_data().map_err(|e| format!("sync {rel}: {e}"))
                    }) {
                        cur.file.take();
                        cleanup_part(&dest, &dst);
                        rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                        record_file_error(&state, slot, e);
                        if !opts.keep_going {
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

                let expected = cur.hasher.finalize();
                if expected.as_bytes() != &hash {
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

                if opts.verify {
                    set_phase(&state, slot, DestPhase::Verifying, None);
                    match hash_file_with_buffer(&tmp, &mut verify_buf, Some(&state)) {
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

                if let Err(e) = retry_io(&state, slot, || commit_part_fast(&dest, &tmp, &dst)) {
                    cleanup_part(&dest, &dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    record_file_error(&state, slot, e);
                    if !opts.keep_going {
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
                }

                if !control.alive.load(Ordering::Acquire) {
                    continue;
                }

                if let Err(e) = journal.append(&fast_state_key(&cur.info)) {
                    record_file_error(&state, slot, e);
                    if !opts.keep_going {
                        control.alive.store(false, Ordering::Release);
                        break;
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
