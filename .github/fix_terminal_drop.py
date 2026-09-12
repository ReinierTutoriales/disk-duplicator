from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

p = Path('src/app/part1.rs')
t = p.read_text(encoding='utf-8')
anchor = '''fn ui_copy_active(engine_running: bool, all_terminal: bool) -> bool {
    engine_running && !all_terminal
}
'''
replacement = anchor + '''
fn drop_input_locked(starting: bool, visual_running: bool) -> bool {
    starting || visual_running
}
'''
t = replace_one(t, anchor, replacement, 'drop input lock helper')
p.write_text(t, encoding='utf-8')

p = Path('src/app/part4.rs')
t = p.read_text(encoding='utf-8')
old = '''        let busy = starting || engine_running;
        let all_successful = all_terminal
'''
new = '''        let busy = starting || engine_running;
        let drop_locked = drop_input_locked(starting, running);
        let all_successful = all_terminal
'''
t = replace_one(t, old, new, 'drop lock state')
t = replace_one(
    t,
    '''            self.accept_drop(dropped_paths, busy);''',
    '''            self.accept_drop(dropped_paths, drop_locked);''',
    'accept drop lock',
)
t = replace_one(
    t,
    '''            let (title, subtitle) = if busy {''',
    '''            let (title, subtitle) = if drop_locked {''',
    'drop hover lock',
)
p.write_text(t, encoding='utf-8')

p = Path('src/app/part5.rs')
t = p.read_text(encoding='utf-8')
anchor = '''    #[test]
    fn terminal_snapshots_end_the_visual_copy_before_engine_cleanup_finishes() {
        assert!(ui_copy_active(true, false));
        assert!(!ui_copy_active(true, true));
        assert!(!ui_copy_active(false, false));
    }
'''
replacement = anchor + '''
    #[test]
    fn terminal_cleanup_does_not_block_a_new_drop() {
        assert!(drop_input_locked(true, false));
        assert!(drop_input_locked(false, true));
        assert!(!drop_input_locked(false, false));

        let visual_running_during_cleanup = ui_copy_active(true, true);
        assert!(!visual_running_during_cleanup);
        assert!(!drop_input_locked(false, visual_running_during_cleanup));
    }
'''
t = replace_one(t, anchor, replacement, 'terminal drop regression test')
p.write_text(t, encoding='utf-8')
