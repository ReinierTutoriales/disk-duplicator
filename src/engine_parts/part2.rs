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
        match item {
            FanoutItem::Begin(info) => {
                state.dests.lock().unwrap()[slot].last_file = info.rel.to_string_lossy().into_owned();
                let dst = dest.join(&info.rel);
                if let Some(parent) = dst.parent() {
                    if let Err(e) = fs::create_dir_all(parent) {
                        record_file_error(&state, slot, format!("mkdir {}: {e}", parent.display()));
                        current = Some(CurrentFile { info, file: None, hasher: blake3::Hasher::new(), copied: 0, failed: true });
                        if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                        continue;
                    }
                }
                cleanup_part(&dst);
                match File::create(part_path(&dst)) {
                    Ok(file) => current = Some(CurrentFile { info, file: Some(file), hasher: blake3::Hasher::new(), copied: 0, failed: false }),
                    Err(e) => {
                        record_file_error(&state, slot, format!(".part {}: {e}", dst.display()));
                        current = Some(CurrentFile { info, file: None, hasher: blake3::Hasher::new(), copied: 0, failed: true });
                        if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                    }
                }
            }
            FanoutItem::Data(buf) => {
                if let Some(cur) = current.as_mut() {
                    if !cur.failed {
                        let rel_display = cur.info.rel.display().to_string();
                        let write_result = if let Some(file) = cur.file.as_mut() {
                            retry_io(&state, slot, || file.write_all(&buf.data).map_err(|e| format!("escritura {rel_display}: {e}")))
                        } else { Err("archivo temporal no disponible".into()) };
                        match write_result {
                            Ok(()) => {
                                cur.hasher.update(&buf.data);
                                cur.copied += buf.data.len() as u64;
                                record_write_progress(&state, slot, buf.data.len() as u64, &mut effective_written, start);
                            }
                            Err(e) => {
                                cur.failed = true;
                                cur.file.take();
                                cleanup_part(&dest.join(&cur.info.rel));
                                rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                                record_file_error(&state, slot, e);
                                if !opts.keep_going { control.alive.store(false, Ordering::Release); }
                            }
                        }
                    }
                }
                control.queue_depth.fetch_sub(1, Ordering::AcqRel);
                state.dests.lock().unwrap()[slot].queue_depth = control.queue_depth.load(Ordering::Acquire);
                if !control.alive.load(Ordering::Acquire) { break; }
            }
            FanoutItem::End { hash } => {
                let Some(mut cur) = current.take() else { continue; };
                let dst = dest.join(&cur.info.rel);
                let tmp = part_path(&dst);
                if cur.failed {
                    cleanup_part(&dst);
                    if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                    continue;
                }
                if cur.copied != cur.info.size {
                    cur.file.take();
                    cleanup_part(&dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    record_file_error(&state, slot, format!("tamaño inesperado en {}", cur.info.rel.display()));
                    if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                    continue;
                }
                if let Some(file) = cur.file.as_mut() {
                    let rel_display = cur.info.rel.display().to_string();
                    if let Err(e) = retry_io(&state, slot, || file.flush().and_then(|_| file.sync_all()).map_err(|e| format!("sync {rel_display}: {e}"))) {
                        cur.file.take();
                        cleanup_part(&dst);
                        rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                        record_file_error(&state, slot, e);
                        if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                        continue;
                    }
                }
                cur.file.take();
                let expected = cur.hasher.finalize();
                if expected.as_bytes() != &hash {
                    cleanup_part(&dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    record_file_error(&state, slot, format!("hash de flujo no coincide: {}", dst.display()));
                    if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                    continue;
                }
                if opts.verify {
                    set_phase(&state, slot, DestPhase::Verifying, None);
                    match hash_file(&tmp, Some(&state)) {
                        Ok(actual) if actual == expected => {}
                        Ok(_) => {
                            cleanup_part(&dst);
                            rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                            record_file_error(&state, slot, format!("BLAKE3 no coincide: {}", dst.display()));
                            set_phase(&state, slot, DestPhase::Copying, None);
                            if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                            continue;
                        }
                        Err(e) => {
                            cleanup_part(&dst);
                            rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                            if e == "Cancelado" { control.alive.store(false, Ordering::Release); break; }
                            record_file_error(&state, slot, e);
                            set_phase(&state, slot, DestPhase::Copying, None);
                            if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                            continue;
                        }
                    }
                    set_phase(&state, slot, DestPhase::Copying, None);
                }
                if let Err(e) = retry_io(&state, slot, || commit_part(&tmp, &dst)) {
                    cleanup_part(&dst);
                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                    record_file_error(&state, slot, e);
                    if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                    continue;
                }
                if let Some(m) = expected_mtime(&cur.info) {
                    if let Err(e) = set_mtime(&dst, m) {
                        record_file_error(&state, slot, e);
                        if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                    }
                }
                if let Err(e) = manifest.append(&cur.info.rel, &expected) {
                    record_file_error(&state, slot, e);
                    if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                }
                if let Err(e) = journal.append(&state_key(&cur.info)) {
                    record_file_error(&state, slot, e);
                    if !opts.keep_going { control.alive.store(false, Ordering::Release); break; }
                }
                record_done(&state, slot);
            }
        }
    }

    if let Some(cur) = current.take() {
        cleanup_part(&dest.join(&cur.info.rel));
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
    if !control.alive.load(Ordering::Acquire) && !opts.keep_going {
        set_phase(&state, slot, DestPhase::Failed, None);
    } else if errs == 0 {
        set_phase(&state, slot, DestPhase::Done, None);
    } else {
        set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es).")));
    }
}
