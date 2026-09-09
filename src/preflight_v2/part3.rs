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
        while handles.iter().any(|h| !h.is_finished()) {
            state.running.store(true, Ordering::Release);
            thread::sleep(Duration::from_millis(20));
        }

        let mut worker_panicked = false;
        for handle in handles {
            if handle.join().is_err() { worker_panicked = true; }
        }

        let source_problem = source_change(&source, &files, &dirs);
        let mut final_errors: Vec<Option<String>> = vec![None; dest_paths.len()];

        if worker_panicked {
            for err in &mut final_errors {
                *err = Some("Un worker terminó de forma inesperada.".into());
            }
        }
        if let Some(message) = source_problem {
            for err in &mut final_errors {
                *err = Some(message.clone());
            }
        }

        if !state.cancel.load(Ordering::Relaxed) {
            for (slot, dest) in dest_paths.iter().enumerate() {
                if final_errors[slot].is_none() {
                    if let Err(e) = validate_destination_result(&source, dest, &files, &dirs, opts.verify) {
                        final_errors[slot] = Some(e);
                    }
                }
            }
        }

        let mut progress = state.dests.lock().unwrap();
        for (slot, dp) in progress.iter_mut().enumerate() {
            if state.cancel.load(Ordering::Relaxed) {
                if !matches!(dp.phase, DestPhase::Cancelled) {
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
                dp.files_err = dp.files_err.saturating_add(1);
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
    let preflight = run_preflight(&source, &dests, opts)?;
    let _planned_bytes: u64 = preflight.plans.iter().map(|p| p.bytes_to_write).sum();
    let (state, handles) = engine_impl::start_job_with_files(
        source.clone(),
        dests.clone(),
        Arc::clone(&preflight.files),
        opts,
    )?;
    let supervisor = supervise_job(
        source,
        dests,
        preflight.files,
        preflight.dirs,
        Arc::clone(&state),
        handles,
        opts,
    );
    Ok((state, vec![supervisor]))
}
