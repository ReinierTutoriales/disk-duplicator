from pathlib import Path
import re


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    result, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{label}: expected one regex match, found {count}")
    return result


# Build every modified file in memory first. Nothing is written if any invariant fails.
p1 = read("src/engine_impl/part1.rs")
p2 = read("src/engine_impl/part2.rs")
p3 = read("src/engine_impl/part3.rs")
p4 = read("src/engine_impl/part4.rs")

p1 = replace_once(
    p1,
    "use std::io::{BufWriter, Read, Write};",
    "use std::io::{BufWriter, Write};\n#[cfg(not(windows))]\nuse std::io::Read;",
    "cfg Read import",
)

p2 = regex_once(
    p2,
    r"fn hash_file_with_buffer\(.*?\n\}\n\nfn validate_part_size",
    '''fn hash_file_with_buffer(
    path: &Path,
    buf: &mut [u8],
    state: Option<&JobState>,
) -> Result<blake3::Hash, String> {
    hash_file_with_buffer_control(path, buf, state, None)
}

fn hash_file_with_buffer_control(
    path: &Path,
    buf: &mut [u8],
    state: Option<&JobState>,
    control: Option<&DestControl>,
) -> Result<blake3::Hash, String> {
    #[cfg(windows)]
    {
        crate::windows_io::hash_file_cancelable(path, buf.len(), || {
            state.is_some_and(|s| s.cancel.load(Ordering::Acquire))
                || control.is_some_and(|c| !c.alive.load(Ordering::Acquire))
        })
        .map_err(|e| {
            if e.kind() == std::io::ErrorKind::Interrupted
                && state.is_some_and(|s| s.cancel.load(Ordering::Acquire))
            {
                "Cancelado".to_owned()
            } else if e.kind() == std::io::ErrorKind::Interrupted
                && control.is_some_and(|c| !c.alive.load(Ordering::Acquire))
            {
                "Destino detenido".to_owned()
            } else {
                format!("verificar: {e}")
            }
        })
    }

    #[cfg(not(windows))]
    {
        let mut f = File::open(path).map_err(|e| format!("verificar: {e}"))?;
        let mut h = blake3::Hasher::new();
        loop {
            if let Some(s) = state {
                if !wait_pause(s) {
                    return Err("Cancelado".into());
                }
            }
            if control.is_some_and(|c| !c.alive.load(Ordering::Acquire)) {
                return Err("Destino detenido".into());
            }
            let n = f.read(buf).map_err(|e| format!("verificar: {e}"))?;
            if n == 0 {
                break;
            }
            h.update(&buf[..n]);
        }
        Ok(h.finalize())
    }
}

fn validate_part_size''',
    "cancelable worker hash",
)

p2 = replace_once(
    p2,
    "let verify_result = hash_file_with_buffer(&tmp, buf, Some(&state));",
    "let verify_result = hash_file_with_buffer_control(&tmp, buf, Some(&state), Some(&control));",
    "worker verify call",
)

# Keep an existing watchdog failure message instead of replacing it with a secondary I/O error.
p2 = replace_once(
    p2,
    '''                    if let Err(e) = sync_result {
                        cur.file.take();
                        cleanup_part(&dest, &dst);
                        rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                        if e != "Cancelado" {
''',
    '''                    if let Err(e) = sync_result {
                        cur.file.take();
                        cleanup_part(&dest, &dst);
                        rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                        if !control.alive.load(Ordering::Acquire) {
                            break;
                        }
                        if e != "Cancelado" {
''',
    "sync watchdog diagnostic",
)

p2 = replace_once(
    p2,
    '''                        Err(e) => {
                            cleanup_part(&dest, &dst);
                            rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                            if e == "Cancelado" {
''',
    '''                        Err(e) => {
                            cleanup_part(&dest, &dst);
                            rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);
                            if !control.alive.load(Ordering::Acquire) {
                                break;
                            }
                            if e == "Cancelado" {
''',
    "verify watchdog diagnostic",
)

p1 = replace_once(
    p1,
    "struct StateJournal {\n",
    '''fn sync_log_data(
    file: &File,
    state: &JobState,
    control: &DestControl,
    label: &str,
) -> Result<(), String> {
    #[cfg(windows)]
    {
        let clone = file
            .try_clone()
            .map_err(|e| format!("{label} sync clone: {e}"))?;
        crate::windows_io::sync_file_cancelable(clone, || {
            state.cancel.load(Ordering::Acquire) || !control.alive.load(Ordering::Acquire)
        })
        .map_err(|e| {
            if e.kind() == std::io::ErrorKind::Interrupted
                && state.cancel.load(Ordering::Acquire)
            {
                "Cancelado".to_owned()
            } else if e.kind() == std::io::ErrorKind::Interrupted
                && !control.alive.load(Ordering::Acquire)
            {
                format!("{label} interrumpido porque el destino dejó de responder")
            } else {
                format!("{label} sync: {e}")
            }
        })
    }

    #[cfg(not(windows))]
    {
        file.sync_data().map_err(|e| format!("{label} sync: {e}"))
    }
}

struct StateJournal {
''',
    "cancelable log sync helper",
)

p1 = regex_once(
    p1,
    r"    fn checkpoint\(&mut self\) -> Result<\(\), String> \{.*?    fn finish\(&mut self\) -> Result<\(\), String> \{ self\.checkpoint\(\) \}\n",
    '''    fn checkpoint(&mut self, state: &JobState, control: &DestControl) -> Result<(), String> {
        if self.pending == 0 {
            return Ok(());
        }
        self.writer.flush().map_err(|e| format!("state flush: {e}"))?;
        sync_log_data(self.writer.get_ref(), state, control, "state")?;
        self.pending = 0;
        self.last_sync = Instant::now();
        Ok(())
    }

    fn finish(&mut self, state: &JobState, control: &DestControl) -> Result<(), String> {
        self.checkpoint(state, control)
    }
''',
    "state journal sync",
)

p1 = regex_once(
    p1,
    r"    fn finish\(&mut self\) -> Result<\(\), String> \{\n        if !self\.dirty.*?impl Drop for ManifestWriter \{\n    fn drop\(&mut self\) \{ let _ = self\.finish\(\); \}\n\}",
    '''    fn finish(&mut self, state: &JobState, control: &DestControl) -> Result<(), String> {
        if !self.dirty {
            return Ok(());
        }
        self.writer.flush().map_err(|e| format!("manifest flush: {e}"))?;
        sync_log_data(self.writer.get_ref(), state, control, "manifest")?;
        self.dirty = false;
        Ok(())
    }
}

impl Drop for ManifestWriter {
    fn drop(&mut self) {
        let _ = self.writer.flush();
    }
}''',
    "manifest sync",
)

p1 = regex_once(
    p1,
    r"fn checkpoint_logs\(.*?\n\}\n\nfn finish_logs\(.*?\n\}",
    '''fn checkpoint_logs(
    manifest: &mut ManifestWriter,
    journal: &mut StateJournal,
    state: &JobState,
    control: &DestControl,
) -> Result<(), String> {
    manifest.finish(state, control)?;
    journal.checkpoint(state, control)
}

fn finish_logs(
    manifest: &mut ManifestWriter,
    journal: &mut StateJournal,
    state: &JobState,
    control: &DestControl,
) -> Result<(), String> {
    manifest.finish(state, control)?;
    journal.finish(state, control)
}''',
    "log helper signatures",
)

p2 = regex_once(
    p2,
    r"                if journal\.needs_checkpoint\(\) \{\n                    if let Err\(e\) = checkpoint_logs\(&mut manifest, &mut journal\) \{.*?                \}\n",
    '''                if journal.needs_checkpoint() {
                    control.enter_operation(OperationPhase::Sync);
                    let checkpoint_result =
                        checkpoint_logs(&mut manifest, &mut journal, &state, &control);
                    control.enter_operation(OperationPhase::Write);
                    if let Err(e) = checkpoint_result {
                        if !control.alive.load(Ordering::Acquire) {
                            break;
                        }
                        record_file_error(&state, slot, e);
                        if !opts.keep_going {
                            control.alive.store(false, Ordering::Release);
                            break;
                        }
                        continue;
                    }
                }
''',
    "checkpoint call",
)

p2 = replace_once(
    p2,
    "    if let Err(e) = finish_logs(&mut manifest, &mut journal) {",
    '''    control.enter_operation(OperationPhase::Sync);
    let finish_result = finish_logs(&mut manifest, &mut journal, &state, &control);
    control.enter_operation(OperationPhase::Write);
    if let Err(e) = finish_result {''',
    "final log call",
)

p2 = replace_once(
    p2,
    '''    if state.cancel.load(Ordering::Relaxed) {
        set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
        return;
    }
''',
    '''    if state.cancel.load(Ordering::Relaxed) {
        set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
        control.alive.store(false, Ordering::Release);
        control.note_progress();
        return;
    }
''',
    "cancel final lifecycle",
)

p2 = replace_once(
    p2,
    '''    } else {
        set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es).")));
    }
}
''',
    '''    } else {
        set_phase(&state, slot, DestPhase::Done, Some(format!("Terminado con {errs} error(es).")));
    }
    control.alive.store(false, Ordering::Release);
    control.note_progress();
}
''',
    "normal final lifecycle",
)

p3 = replace_once(
    p3,
    "fn mark_skipped_all(state: &JobState, info: &FileInfo, mask: &[bool]) {\n",
    '''fn watch_workers_after_input_closed(
    controls: &[Arc<DestControl>],
    state: &JobState,
    pending: &mut PendingQueues,
) {
    let mut seen_progress: Vec<u64> = controls.iter().map(|control| control.progress_seq()).collect();
    let mut last_progress: Vec<Instant> = (0..controls.len()).map(|_| Instant::now()).collect();

    while controls.iter().any(|control| control.alive.load(Ordering::Acquire)) {
        if state.cancel.load(Ordering::Acquire) {
            for control in controls {
                control.alive.store(false, Ordering::Release);
                control.note_progress();
            }
        }
        for slot in 0..controls.len() {
            if !controls[slot].alive.load(Ordering::Acquire) {
                continue;
            }
            let current = controls[slot].progress_seq();
            if current != seen_progress[slot] {
                seen_progress[slot] = current;
                last_progress[slot] = Instant::now();
            }
            if controls[slot].stall_timed_out(last_progress[slot]) {
                fail_stalled_destination(slot, pending, controls, state);
            }
        }
        thread::sleep(Duration::from_millis(20));
    }
}

fn mark_skipped_all(state: &JobState, info: &FileInfo, mask: &[bool]) {
''',
    "tail watchdog helper",
)

p3 = replace_once(
    p3,
    '''        );
        drop(senders);
''',
    '''        );
        drop(senders);
        watch_workers_after_input_closed(&controls, &state, &mut pending);
''',
    "tail watchdog invocation",
)

p4 = replace_once(
    p4,
    '''    #[test]
    fn stall_thresholds_allow_slow_storage() {
''',
    '''    #[test]
    fn tail_watchdog_fails_stalled_final_operation() {
        let root = temp_dir("tail-watchdog");
        let state = worker_state(&root, 0);
        let control = Arc::new(DestControl::new());
        *control.operation.lock().unwrap() = (
            OperationPhase::Verify,
            Instant::now() - LONG_OP_THRESHOLD - Duration::from_secs(1),
        );
        let controls = vec![Arc::clone(&control)];
        let mut pending: PendingQueues = vec![std::collections::VecDeque::new()];

        watch_workers_after_input_closed(&controls, &state, &mut pending);

        assert!(!control.alive.load(Ordering::Acquire));
        let snap = state.snapshot();
        assert_eq!(snap[0].phase, DestPhase::Failed);
        assert!(snap[0]
            .error
            .as_deref()
            .is_some_and(|message| message.contains("Destino atascado")));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn stall_thresholds_allow_slow_storage() {
''',
    "tail watchdog regression",
)

# Transactional write after all source-shape checks succeeded.
write("src/engine_impl/part1.rs", p1)
write("src/engine_impl/part2.rs", p2)
write("src/engine_impl/part3.rs", p3)
write("src/engine_impl/part4.rs", p4)
