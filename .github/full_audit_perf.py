from pathlib import Path
import re


def replace_one(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)


# 1) Engine: remove redundant per-file physical BLAKE3 pass.
p = Path('src/engine_impl/part2.rs')
t = p.read_text(encoding='utf-8')
t = replace_one(
    t,
    '''    const VERIFY_BUFFER: usize = 8 * 1024 * 1024;\n\n''',
    '',
    'remove worker verify buffer constant',
)
t = replace_one(
    t,
    '''    let mut verify_buf = opts.verify.then(|| vec![0u8; VERIFY_BUFFER]);\n''',
    '',
    'remove worker verify buffer allocation',
)
verify_block = re.compile(
    r'''\n                if let Some\(buf\) = verify_buf\.as_mut\(\) \{.*?\n                \}\n\n                if !control\.alive\.load\(Ordering::Acquire\) \{''',
    re.DOTALL,
)
t, count = verify_block.subn(
    '''\n                // Physical BLAKE3 is deliberately deferred to the final supervisor pass.\n                // Doing it here stalls this destination, fills the bounded FAN-OUT queues,\n                // and throttles every other destination behind the verifier. The journal\n                // and manifest still record the source hash, and preflight re-validates\n                // resumed entries physically before trusting them.\n\n                if !control.alive.load(Ordering::Acquire) {''',
    t,
    count=1,
)
if count != 1:
    raise RuntimeError(f'remove per-file physical verify: expected 1 match, got {count}')
old_terminal = '''    let errs = state.dests.lock().unwrap()[slot].files_err;
    if !control.alive.load(Ordering::Acquire) {
        set_phase(&state, slot, DestPhase::Failed, None);
    } else if errs == 0 {
        set_phase(&state, slot, DestPhase::Done, None);
    } else {
        set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es).")));
    }
'''
new_terminal = '''    let errs = state.dests.lock().unwrap()[slot].files_err;
    if !control.alive.load(Ordering::Acquire) {
        set_phase(&state, slot, DestPhase::Failed, None);
    } else if opts.verify {
        // The copy worker is finished, but the job is not complete until the supervisor
        // performs the final physical BLAKE3 validation. Keeping the phase non-terminal
        // prevents the UI from announcing completion or accepting a replacement job early.
        set_phase(&state, slot, DestPhase::Verifying, None);
    } else if errs == 0 {
        set_phase(&state, slot, DestPhase::Done, None);
    } else {
        set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es).")));
    }
'''
t = replace_one(t, old_terminal, new_terminal, 'worker terminal phase')
p.write_text(t, encoding='utf-8')


# 2) Supervisor: normalize the final validation phase and clear stale speed/file text.
p = Path('src/preflight/part3.rs')
t = p.read_text(encoding='utf-8')
anchor = '''        if state.cancel.load(Ordering::Acquire) {
            let mut progress = state.dests.lock().unwrap();
            for dp in progress.iter_mut() {
                if dp.phase != DestPhase::Failed {
                    dp.phase = DestPhase::Cancelled;
                    dp.error = Some("Cancelado".into());
                }
            }
            drop(progress);
            state.running.store(false, Ordering::Release);
            return;
        }

        let source_problem = source_change(
'''
replacement = '''        if state.cancel.load(Ordering::Acquire) {
            let mut progress = state.dests.lock().unwrap();
            for dp in progress.iter_mut() {
                if dp.phase != DestPhase::Failed {
                    dp.phase = DestPhase::Cancelled;
                    dp.error = Some("Cancelado".into());
                }
            }
            drop(progress);
            state.running.store(false, Ordering::Release);
            return;
        }

        if opts.verify {
            let mut progress = state.dests.lock().unwrap();
            for dp in progress.iter_mut() {
                if !matches!(dp.phase, DestPhase::Failed | DestPhase::Cancelled) {
                    dp.phase = DestPhase::Verifying;
                    dp.bps = 0.0;
                    dp.bps_recent = 0.0;
                    dp.last_file.clear();
                }
            }
        }

        let source_problem = source_change(
'''
t = replace_one(t, anchor, replacement, 'final validation phase normalization')
p.write_text(t, encoding='utf-8')


# 3) UI helpers: only Copying has throughput; FAN-OUT rate is the slowest active sink.
p = Path('src/app/part1.rs')
t = p.read_text(encoding='utf-8')
old = '''fn visible_bps(bps: f64, last_tick: Instant, terminal: bool, paused: bool) -> f64 {
    if terminal || paused {
        0.0
    } else {
        shown_bps(bps, last_tick)
    }
}
'''
new = '''fn visible_bps(bps: f64, last_tick: Instant, phase: DestPhase, paused: bool) -> f64 {
    if paused || phase != DestPhase::Copying {
        0.0
    } else {
        shown_bps(bps, last_tick)
    }
}

fn logical_fanout_bps(speeds: impl IntoIterator<Item = f64>) -> f64 {
    let mut speeds = speeds.into_iter();
    let Some(first) = speeds.next() else {
        return 0.0;
    };
    speeds.fold(first, f64::min).max(0.0)
}

fn can_start_new_job(starting: bool, engine_running: bool) -> bool {
    !starting && !engine_running
}
'''
t = replace_one(t, old, new, 'throughput helpers')
p.write_text(t, encoding='utf-8')


# 4) UI: show sustainable FAN-OUT speed, keep aggregate writes in tooltip, and lock Start
# until the previous supervisor has actually released resources.
p = Path('src/app/part4.rs')
t = p.read_text(encoding='utf-8')
old = '''                    let total_bps = if all_terminal || paused {
                        0.0
                    } else {
                        snaps
                            .iter()
                            .map(|progress| {
                                shown_bps(progress.bps_recent, progress.last_tick)
                            })
                            .sum()
                    };
'''
new = '''                    let aggregate_write_bps: f64 = snaps
                        .iter()
                        .map(|progress| {
                            visible_bps(
                                progress.bps_recent,
                                progress.last_tick,
                                progress.phase,
                                paused,
                            )
                        })
                        .sum();
                    let fanout_bps = logical_fanout_bps(
                        snaps
                            .iter()
                            .filter(|progress| progress.phase == DestPhase::Copying)
                            .map(|progress| {
                                visible_bps(
                                    progress.bps_recent,
                                    progress.last_tick,
                                    progress.phase,
                                    paused,
                                )
                            }),
                    );
'''
t = replace_one(t, old, new, 'overall throughput metrics')

old = '''                    } else if !starting {
                        let button = egui::Button::new(
'''
new = '''                    } else if can_start_new_job(starting, engine_running) {
                        let button = egui::Button::new(
'''
t = replace_one(t, old, new, 'start button supervisor lock')

old = '''                    } else {
                        ui.spinner();
                        ui.weak("Preparando…");
                    }
'''
new = '''                    } else {
                        ui.spinner();
                        ui.weak(if starting { "Preparando…" } else { "Finalizando…" });
                    }
'''
t = replace_one(t, old, new, 'start button finalizing label')

old = '''                                    ui.weak(format!(
                                        "{} · ETA {eta}",
                                        format_bytes(job.bytes_total.load(Ordering::Relaxed))
                                    ));
                                    ui.label(
                                        RichText::new(format!("Vel. {}", format_bps(total_bps)))
                                            .strong()
                                            .size(11.5),
                                    );
'''
new = '''                                    let total_size =
                                        format_bytes(job.bytes_total.load(Ordering::Relaxed));
                                    if verifying {
                                        ui.weak(format!("{total_size} · validación final BLAKE3"));
                                    } else {
                                        ui.weak(format!("{total_size} · ETA copia {eta}"));
                                    }
                                    let speed_text = if verifying {
                                        "FAN-OUT —".to_owned()
                                    } else {
                                        format!("FAN-OUT {}", format_bps(fanout_bps))
                                    };
                                    ui.label(RichText::new(speed_text).strong().size(11.5))
                                        .on_hover_text(format!(
                                            "Escritura agregada a destinos: {}",
                                            format_bps(aggregate_write_bps)
                                        ));
'''
t = replace_one(t, old, new, 'fanout speed display')

old = '''                                                    let terminal = matches!(
                                                        progress.phase,
                                                        DestPhase::Done
                                                            | DestPhase::Failed
                                                            | DestPhase::Cancelled
                                                    );
                                                    let speed = visible_bps(
                                                        progress.bps_recent,
                                                        progress.last_tick,
                                                        terminal,
                                                        paused,
                                                    );
'''
new = '''                                                    let speed = visible_bps(
                                                        progress.bps_recent,
                                                        progress.last_tick,
                                                        progress.phase,
                                                        paused,
                                                    );
'''
t = replace_one(t, old, new, 'per-destination phase-aware speed')

old = '''                                                    let terminal = matches!(
                                                        progress.phase,
                                                        DestPhase::Done
                                                            | DestPhase::Failed
                                                            | DestPhase::Cancelled
                                                    );
                                                    let last_file = if terminal
                                                        || progress.last_file.is_empty()
                                                    {
'''
new = '''                                                    let last_file = if progress.phase
                                                        != DestPhase::Copying
                                                        || progress.last_file.is_empty()
                                                    {
'''
t = replace_one(t, old, new, 'hide stale file outside copy phase')
p.write_text(t, encoding='utf-8')


# 5) Keep product copy accurate now that single-file sources are supported.
p = Path('src/app/part3.rs')
t = p.read_text(encoding='utf-8')
t = t.replace('Una carpeta · múltiples destinos', 'Archivo o carpeta · múltiples destinos')
p.write_text(t, encoding='utf-8')


# 6) Tests: worker must wait in Verifying for supervisor and UI metrics must not inflate FAN-OUT.
p = Path('src/engine_impl/part4.rs')
t = p.read_text(encoding='utf-8')
t = replace_one(
    t,
    '        assert_eq!(state.snapshot()[0].phase, DestPhase::Done);\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn format_is_sane()',
    '        assert_eq!(state.snapshot()[0].phase, DestPhase::Verifying);\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn verify_worker_defers_physical_validation_to_supervisor() {\n        let root = temp_dir("deferred-final-verify");\n        let source = root.join("src");\n        let dest = root.join("dst");\n        fs::create_dir_all(&source).unwrap();\n        fs::create_dir_all(&dest).unwrap();\n        let data = vec![0x5Au8; 2 * 1024 * 1024];\n        let path = source.join("large.bin");\n        fs::write(&path, &data).unwrap();\n        let meta = fs::metadata(&path).unwrap();\n        let files = Arc::new(vec![FileInfo {\n            rel: PathBuf::from("large.bin"),\n            size: meta.len(),\n            mtime_ns: metadata_mtime_ns(&meta),\n        }]);\n        let opts = CopyOpts { verify: true, skip_same: false, keep_going: true };\n        let (state, handles) = start_job_with_files(\n            source,\n            vec![dest.clone()],\n            files,\n            Arc::new(Vec::new()),\n            opts,\n        ).unwrap();\n        for handle in handles { handle.join().unwrap(); }\n\n        assert_eq!(fs::read(dest.join("large.bin")).unwrap(), data);\n        let snap = state.snapshot();\n        assert_eq!(snap[0].phase, DestPhase::Verifying);\n        assert_eq!(snap[0].files_done, 1);\n        assert_eq!(snap[0].files_err, 0);\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn format_is_sane()',
    'engine deferred verification tests',
)
p.write_text(t, encoding='utf-8')

p = Path('src/app/part5.rs')
t = p.read_text(encoding='utf-8')
old = '''    #[test]
    fn terminal_and_paused_speed_is_always_zero() {
        let now = Instant::now();
        assert_eq!(visible_bps(900_000_000.0, now, true, false), 0.0);
        assert_eq!(visible_bps(900_000_000.0, now, false, true), 0.0);
        assert!(visible_bps(900_000_000.0, now, false, false) > 0.0);
    }
'''
new = '''    #[test]
    fn only_copying_phase_reports_throughput() {
        let now = Instant::now();
        assert_eq!(visible_bps(900_000_000.0, now, DestPhase::Done, false), 0.0);
        assert_eq!(visible_bps(900_000_000.0, now, DestPhase::Verifying, false), 0.0);
        assert_eq!(visible_bps(900_000_000.0, now, DestPhase::Copying, true), 0.0);
        assert!(visible_bps(900_000_000.0, now, DestPhase::Copying, false) > 0.0);
    }

    #[test]
    fn fanout_rate_is_not_inflated_by_destination_count() {
        let logical = logical_fanout_bps([108.1, 108.7, 108.8, 108.6]);
        assert!((logical - 108.1).abs() < f64::EPSILON);
        assert_eq!(logical_fanout_bps([]), 0.0);
    }

    #[test]
    fn previous_supervisor_blocks_new_job_but_not_drop_preparation() {
        assert!(!can_start_new_job(false, true));
        assert!(can_start_new_job(false, false));
        assert!(!can_start_new_job(true, false));
        assert!(!drop_input_locked(false, false));
    }
'''
t = replace_one(t, old, new, 'UI throughput regression tests')
p.write_text(t, encoding='utf-8')

print('full audit performance patch applied')
