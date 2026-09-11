from pathlib import Path

p = Path('.github/audit_stability_patch.py')
s = p.read_text(encoding='utf-8')
lines = s.splitlines()

start = next(i for i, line in enumerate(lines) if line.startswith("old='''    if state.cancel.load(Ordering::Relaxed)"))
end = next(i for i, line in enumerate(lines[start:], start) if "worker final lifecycle" in line)

replacement = [
    "tail_start = p2.index('    if let Err(e) = finish_result {')",
    "tail_new = r'''    if let Err(e) = finish_result {",
    "        if state.cancel.load(Ordering::Acquire) {",
    "            set_phase(&state, slot, DestPhase::Cancelled, Some(\"Cancelado\".into()));",
    "        } else {",
    "            set_phase(&state, slot, DestPhase::Failed, Some(e));",
    "        }",
    "        control.alive.store(false, Ordering::Release);",
    "        control.note_progress();",
    "        return;",
    "    }",
    "",
    "    if state.cancel.load(Ordering::Acquire) {",
    "        set_phase(&state, slot, DestPhase::Cancelled, Some(\"Cancelado\".into()));",
    "        control.alive.store(false, Ordering::Release);",
    "        control.note_progress();",
    "        return;",
    "    }",
    "",
    "    let errs = state.dests.lock().unwrap()[slot].files_err;",
    "    if !control.alive.load(Ordering::Acquire) {",
    "        set_phase(&state, slot, DestPhase::Failed, None);",
    "    } else if errs == 0 {",
    "        set_phase(&state, slot, DestPhase::Done, None);",
    "    } else {",
    "        set_phase(&state, slot, DestPhase::Done, Some(format!(\"Terminado con {errs} error(es).\")));",
    "    }",
    "    control.alive.store(false, Ordering::Release);",
    "    control.note_progress();",
    "}",
    "'''",
    "p2 = p2[:tail_start] + tail_new",
]
lines[start:end + 1] = replacement
s = '\n'.join(lines) + '\n'

old = '''        if state.cancel.load(Ordering::Acquire) {\\n            for c in controls { c.alive.store(false, Ordering::Release); c.note_progress(); }\\n        }\\n'''
new = '''        if state.cancel.load(Ordering::Acquire) {\\n            thread::sleep(Duration::from_millis(20));\\n            continue;\\n        }\\n'''
if s.count(old) != 1:
    raise RuntimeError(f'watcher cancellation helper: expected 1, got {s.count(old)}')
s = s.replace(old, new, 1)

old = '''                    if let Err(e) = checkpoint_result {\\n                        if !control.alive.load(Ordering::Acquire) { break; }\\n                        record_file_error(&state, slot, e);\\n'''
new = '''                    if let Err(e) = checkpoint_result {\\n                        if state.cancel.load(Ordering::Acquire) { break; }\\n                        if !control.alive.load(Ordering::Acquire) { break; }\\n                        record_file_error(&state, slot, e);\\n'''
if s.count(old) != 1:
    raise RuntimeError(f'checkpoint cancellation helper: expected 1, got {s.count(old)}')
s = s.replace(old, new, 1)

p.write_text(s, encoding='utf-8')
