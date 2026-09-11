from pathlib import Path
import re

p = Path('.github/audit_stability_patch.py')
s = p.read_text(encoding='utf-8')

# Replace the fragile final-lifecycle matcher with a structural tail replacement.
pattern = r"old='''    if state\.cancel\.load\(Ordering::Relaxed\).*?p2=one\(p2,old,new,'worker final lifecycle'\)"
replacement = r'''tail_start = p2.index("    if let Err(e) = finish_result {")
p2 = p2[:tail_start] + '''\'''''    if let Err(e) = finish_result {
        if state.cancel.load(Ordering::Acquire) {
            set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
        } else {
            set_phase(&state, slot, DestPhase::Failed, Some(e));
        }
        control.alive.store(false, Ordering::Release);
        control.note_progress();
        return;
    }

    if state.cancel.load(Ordering::Acquire) {
        set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));
        control.alive.store(false, Ordering::Release);
        control.note_progress();
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
    control.alive.store(false, Ordering::Release);
    control.note_progress();
}
'''\''''' '''
s, n = re.subn(pattern, replacement, s, count=1, flags=re.S)
if n != 1:
    raise RuntimeError(f'final lifecycle helper patch: expected 1, got {n}')

# Global cancellation is not a destination failure; native I/O already observes state.cancel.
s = s.replace(
    '''        if state.cancel.load(Ordering::Acquire) {\\n            for c in controls { c.alive.store(false, Ordering::Release); c.note_progress(); }\\n        }\\n''',
    '''        if state.cancel.load(Ordering::Acquire) {\\n            thread::sleep(Duration::from_millis(20));\\n            continue;\\n        }\\n''',
    1,
)

# Do not persist a user cancellation as a file error during journal/manifest checkpointing.
s = s.replace(
    '''                    if let Err(e) = checkpoint_result {\\n                        if !control.alive.load(Ordering::Acquire) { break; }\\n                        record_file_error(&state, slot, e);\\n''',
    '''                    if let Err(e) = checkpoint_result {\\n                        if state.cancel.load(Ordering::Acquire) { break; }\\n                        if !control.alive.load(Ordering::Acquire) { break; }\\n                        record_file_error(&state, slot, e);\\n''',
    1,
)

p.write_text(s, encoding='utf-8')
