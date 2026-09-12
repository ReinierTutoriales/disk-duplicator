from pathlib import Path


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one anchor, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


part1 = Path("src/app/part1.rs")
part2 = Path("src/app/part2.rs")
part3 = Path("src/app/part3.rs")
part4 = Path("src/app/part4.rs")
engine1 = Path("src/engine_impl/part1.rs")
engine2 = Path("src/engine_impl/part2.rs")
engine3 = Path("src/engine_impl/part3.rs")

replace_once(
    part1,
    "type StartResult = Result<(Arc<JobState>, Vec<JoinHandle<()>>), String>;\n",
    "type StartResult = Result<(Arc<JobState>, Vec<JoinHandle<()>>), String>;\ntype PathValidationResult = (u64, Vec<String>);\n",
    "path validation result alias",
)

replace_once(
    part2,
    "    startup_rx: Option<mpsc::Receiver<StartResult>>,\n",
    "    startup_rx: Option<mpsc::Receiver<StartResult>>,\n    path_validation_rx: Option<mpsc::Receiver<PathValidationResult>>,\n    validated_paths_key: Option<u64>,\n",
    "path validation state fields",
)

replace_once(
    part3,
    "            startup_rx: None,\n            show_credits: false,",
    "            startup_rx: None,\n            path_validation_rx: None,\n            validated_paths_key: None,\n            show_credits: false,",
    "path validation constructor",
)
replace_once(
    part3,
    "        self.startup_rx = None;\n        self.pending_drop = None;\n        self.path_errors.clear();",
    "        self.startup_rx = None;\n        self.path_validation_rx = None;\n        self.validated_paths_key = None;\n        self.pending_drop = None;\n        self.path_errors.clear();",
    "path validation reset",
)

old_validate = '''    fn validate_paths(&self) -> Vec<String> {
        let mut errors = Vec::new();
        let source = self.source.trim();
        if !source.is_empty() {
            let path = PathBuf::from(source);
            if !path.exists() {
                errors.push("El origen no existe.".to_owned());
            } else if !path.is_dir() && !path.is_file() {
                errors.push("El origen no es un archivo regular ni una carpeta.".to_owned());
            }
        }

        for (index, dest) in self.dests.iter().enumerate() {
            let path = PathBuf::from(dest.trim());
            if !path.exists() {
                errors.push(format!("El destino {} no está disponible.", index + 1));
            } else if !path.is_dir() {
                errors.push(format!("El destino {} no es una carpeta.", index + 1));
            }
        }
        errors
    }
'''
new_validate = '''    fn validate_paths_snapshot(source: &str, dests: &[String]) -> Vec<String> {
        let mut errors = Vec::new();
        let source = source.trim();
        if !source.is_empty() {
            let path = PathBuf::from(source);
            match std::fs::metadata(&path) {
                Ok(meta) if meta.is_dir() || meta.is_file() => {}
                Ok(_) => errors.push("El origen no es un archivo regular ni una carpeta.".to_owned()),
                Err(_) => errors.push("El origen no existe o no está disponible.".to_owned()),
            }
        }

        for (index, dest) in dests.iter().enumerate() {
            let path = PathBuf::from(dest.trim());
            match std::fs::metadata(&path) {
                Ok(meta) if meta.is_dir() => {}
                Ok(_) => errors.push(format!("El destino {} no es una carpeta.", index + 1)),
                Err(_) => errors.push(format!("El destino {} no está disponible.", index + 1)),
            }
        }
        errors
    }

    fn start_path_validation(&mut self, key: u64, ctx: &egui::Context) {
        let source = self.source.clone();
        let dests = self.dests.clone();
        let repaint = ctx.clone();
        let (tx, rx) = mpsc::channel();
        self.path_validation_rx = Some(rx);
        thread::spawn(move || {
            let errors = Self::validate_paths_snapshot(&source, &dests);
            let _ = tx.send((key, errors));
            repaint.request_repaint();
        });
    }

    fn poll_path_validation(&mut self) {
        let outcome = self.path_validation_rx.as_ref().and_then(|rx| match rx.try_recv() {
            Ok(result) => Some(Ok(result)),
            Err(mpsc::TryRecvError::Disconnected) => Some(Err(())),
            Err(mpsc::TryRecvError::Empty) => None,
        });
        let Some(outcome) = outcome else {
            return;
        };
        self.path_validation_rx = None;
        match outcome {
            Ok((key, errors)) if key == self.paths_key() => {
                self.path_errors = errors;
                self.validated_paths_key = Some(key);
            }
            Ok(_) => {}
            Err(()) => {
                self.validated_paths_key = None;
            }
        }
    }
'''
replace_once(part3, old_validate, new_validate, "async path validation")

replace_once(
    part3,
    '''        if let Some(error) = self.validate_paths().into_iter().next() {
            self.flash_error(error);
            return;
        }

        let source = PathBuf::from(self.source.trim());
''',
    '''        if self.validated_paths_key != Some(self.paths_key()) {
            self.flash_error("Las rutas todavía se están validando.".to_owned());
            return;
        }

        let source = PathBuf::from(self.source.trim());
''',
    "remove synchronous start validation",
)
replace_once(
    part3,
    '''        self.error_flash_until = None;
        self.status = "Analizando origen y destinos…".to_owned();
        self.startup_rx = Some(rx);
''',
    '''        self.error_flash_until = None;
        self.validated_paths_key = None;
        self.path_validation_rx = None;
        self.status = "Analizando origen y destinos…".to_owned();
        self.startup_rx = Some(rx);
''',
    "invalidate validation when starting",
)
replace_once(
    part3,
    ".rounding(egui::Rounding::same(9.0))",
    ".rounding(egui::Rounding::same(FLUENT_RADIUS_MD))",
    "theme card Fluent radius",
)

replace_once(
    part4,
    "        self.poll_startup();\n",
    "        self.poll_startup();\n        self.poll_path_validation();\n",
    "poll path validation",
)
replace_once(
    part4,
    "            ctx.request_repaint_after(THEME_CHECK_INTERVAL);",
    "            ctx.request_repaint_after(PATH_CHECK_INTERVAL);",
    "idle repaint for path refresh",
)
old_path_block = '''        let key = self.paths_key();
        if key != self.paths_key || self.last_path_check.elapsed() >= PATH_CHECK_INTERVAL {
            self.paths_key = key;
            self.last_path_check = Instant::now();
            self.path_errors = if busy {
                Vec::new()
            } else {
                self.validate_paths()
            };
        }

        let path_error_count = self.path_errors.len();
        let start_disabled =
            start_disabled_reason(&self.source, self.dests.len(), path_error_count);
        let ready_to_start = start_disabled.is_none();
'''
new_path_block = '''        let key = self.paths_key();
        let key_changed = key != self.paths_key;
        if key_changed {
            self.paths_key = key;
            self.path_errors.clear();
            self.validated_paths_key = None;
            // Drop a stale receiver so a slow old UNC/removable path cannot block
            // validation of newly selected paths. Its worker exits when the OS call returns.
            self.path_validation_rx = None;
        }
        if busy {
            self.path_errors.clear();
            self.path_validation_rx = None;
        } else if key_changed
            || (self.path_validation_rx.is_none()
                && self.last_path_check.elapsed() >= PATH_CHECK_INTERVAL)
        {
            self.last_path_check = Instant::now();
            self.start_path_validation(key, ctx);
        }

        let path_error_count = self.path_errors.len();
        let mut start_disabled =
            start_disabled_reason(&self.source, self.dests.len(), path_error_count);
        if start_disabled.is_none() && self.validated_paths_key != Some(key) {
            start_disabled = Some("Validando rutas…");
        }
        let ready_to_start = start_disabled.is_none();
'''
replace_once(part4, old_path_block, new_path_block, "nonblocking path validation UI")

old_record = '''fn record_write_progress(
    state: &JobState,
    slot: usize,
    size: u64,
    effective_written: &mut u64,
    start: Instant,
) {
'''
new_record = '''fn record_write_progress(
    state: &JobState,
    slot: usize,
    size: u64,
    queue_depth: usize,
    effective_written: &mut u64,
    start: Instant,
) {
'''
replace_once(engine1, old_record, new_record, "progress queue depth argument")
replace_once(
    engine1,
    "    dp.written = dp.written.saturating_add(size);\n\n    let elapsed",
    "    dp.written = dp.written.saturating_add(size);\n    dp.queue_depth = queue_depth;\n\n    let elapsed",
    "progress queue depth update",
)

old_data = '''                        match result {
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
'''
new_data = '''                        let queue_depth = control
                            .queue_depth
                            .fetch_sub(1, Ordering::AcqRel)
                            .saturating_sub(1);
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
                                    queue_depth,
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
                                state.dests.lock().unwrap()[slot].queue_depth = queue_depth;
                                if !opts.keep_going || state.cancel.load(Ordering::Acquire) {
                                    control.alive.store(false, Ordering::Release);
                                }
                            }
                        }
                    } else {
                        let queue_depth = control
                            .queue_depth
                            .fetch_sub(1, Ordering::AcqRel)
                            .saturating_sub(1);
                        state.dests.lock().unwrap()[slot].queue_depth = queue_depth;
                    }
                } else {
                    let queue_depth = control
                        .queue_depth
                        .fetch_sub(1, Ordering::AcqRel)
                        .saturating_sub(1);
                    state.dests.lock().unwrap()[slot].queue_depth = queue_depth;
                }
                if !control.alive.load(Ordering::Acquire) { break; }
'''
replace_once(engine2, old_data, new_data, "writer progress lock consolidation")

old_reader_tail = '''        if counts_data {
            state.dests.lock().unwrap()[slot].queue_depth =
                controls[slot].queue_depth.load(Ordering::Acquire);
        }
    }
}
'''
new_reader_tail = '''    }

    if counts_data {
        let mut dests = state.dests.lock().unwrap();
        for (slot, control) in controls.iter().enumerate() {
            dests[slot].queue_depth = control.queue_depth.load(Ordering::Acquire);
        }
    }
}
'''
replace_once(engine3, old_reader_tail, new_reader_tail, "reader queue depth lock consolidation")

print("runtime responsiveness optimization applied")
