fn validate_destinations_parallel(
    source: &Path,
    dest_paths: &[PathBuf],
    files: &[PlannedFile],
    dirs: &[PathBuf],
    verify: bool,
    expected_hashes: &std::collections::HashMap<PathBuf, [u8; 32]>,
    current_errors: &[Option<String>],
) -> Vec<(usize, Result<(), String>)> {
    thread::scope(|scope| {
        let mut checks = Vec::new();
        for (slot, dest) in dest_paths.iter().enumerate() {
            if current_errors[slot].is_some() {
                continue;
            }
            checks.push((
                slot,
                scope.spawn(move || {
                    validate_destination_result_with_hashes(
                        source,
                        dest,
                        files,
                        dirs,
                        verify,
                        expected_hashes,
                    )
                }),
            ));
        }

        checks
            .into_iter()
            .map(|(slot, handle)| {
                let result = handle.join().unwrap_or_else(|_| {
                    Err("La verificación final del destino terminó inesperadamente.".into())
                });
                (slot, result)
            })
            .collect()
    })
}

fn supervise_job(
    source: PathBuf,
    dest_paths: Vec<PathBuf>,
    files: Arc<Vec<PlannedFile>>,
    dirs: Arc<Vec<PathBuf>>,
    state: Arc<JobState>,
    handles: Vec<JoinHandle<()>>,
    opts: CopyOpts,
) -> JoinHandle<()> {
    thread::spawn(move || {
        loop {
            state.running.store(true, Ordering::Release);
            if handles.iter().all(JoinHandle::is_finished) {
                break;
            }
            thread::sleep(Duration::from_millis(20));
        }

        let mut worker_panicked = false;
        for handle in handles {
            if handle.join().is_err() {
                worker_panicked = true;
            }
        }

        let source_problem = source_change(&source, &files, &dirs);
        let mut final_errors: Vec<Option<String>> = vec![None; dest_paths.len()];

        {
            let progress = state.dests.lock().unwrap();
            for (slot, dp) in progress.iter().enumerate() {
                if dp.phase == DestPhase::Failed {
                    final_errors[slot] = Some(
                        dp.error
                            .clone()
                            .unwrap_or_else(|| "El destino fue desconectado del FAN-OUT.".into()),
                    );
                }
            }
        }

        if worker_panicked {
            for err in &mut final_errors {
                if err.is_none() {
                    *err = Some("Un worker terminó de forma inesperada.".into());
                }
            }
        }
        if let Some(message) = source_problem {
            for err in &mut final_errors {
                if err.is_none() {
                    *err = Some(message.clone());
                }
            }
        }

        if !state.cancel.load(Ordering::Relaxed) {
            let reader_hashes = state.reader_hashes.lock().unwrap().clone();
            let expected_hashes = if opts.verify {
                match final_source_hashes(&source, &files, &reader_hashes) {
                    Ok(hashes) => hashes,
                    Err(e) => {
                        for err in &mut final_errors {
                            if err.is_none() {
                                *err = Some(e.clone());
                            }
                        }
                        std::collections::HashMap::new()
                    }
                }
            } else {
                reader_hashes
            };

            for (slot, result) in validate_destinations_parallel(
                &source,
                &dest_paths,
                &files,
                &dirs,
                opts.verify,
                &expected_hashes,
                &final_errors,
            ) {
                if let Err(e) = result {
                    final_errors[slot] = Some(e);
                }
            }
        }

        let mut progress = state.dests.lock().unwrap();
        for (slot, dp) in progress.iter_mut().enumerate() {
            if state.cancel.load(Ordering::Relaxed) {
                if !matches!(dp.phase, DestPhase::Cancelled | DestPhase::Failed) {
                    dp.phase = DestPhase::Cancelled;
                    dp.error = Some("Cancelado".into());
                }
                continue;
            }

            if dp.files_err > 0 && final_errors[slot].is_none() {
                final_errors[slot] = Some(format!(
                    "La copia terminó con {} error(es); no se considera completa.",
                    dp.files_err
                ));
            }

            if let Some(message) = final_errors[slot].take() {
                dp.phase = DestPhase::Failed;
                if dp.files_err == 0 {
                    dp.files_err = 1;
                }
                dp.error = Some(message);
            } else {
                dp.phase = DestPhase::Done;
                dp.error = None;
                dp.written = dp.total;
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
    let (preflight, verified_skips) = run_preflight(&source, &dests, opts)?;
    let (state, handles) = engine_impl::start_job_with_files_preverified(
        preflight.source.clone(),
        preflight.dests.clone(),
        Arc::clone(&preflight.files),
        Arc::clone(&preflight.dirs),
        opts,
        verified_skips,
    )?;
    let supervisor = supervise_job(
        preflight.source,
        preflight.dests,
        preflight.files,
        preflight.dirs,
        Arc::clone(&state),
        handles,
        opts,
    );
    Ok((state, vec![supervisor]))
}
