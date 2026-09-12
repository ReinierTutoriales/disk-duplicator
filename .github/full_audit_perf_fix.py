from pathlib import Path


def replace_one(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

p = Path('src/engine_impl/part1.rs')
t = p.read_text(encoding='utf-8')
t = replace_one(
    t,
    '''enum OperationPhase {
    Write,
    Sync,
    Verify,
    Commit,
}
''',
    '''enum OperationPhase {
    Write,
    Sync,
    Commit,
}
''',
    'remove obsolete Verify operation phase',
)
t = replace_one(
    t,
    '''            OperationPhase::Sync | OperationPhase::Verify | OperationPhase::Commit => {
                started.elapsed() >= LONG_OP_THRESHOLD
            }
''',
    '''            OperationPhase::Sync | OperationPhase::Commit => {
                started.elapsed() >= LONG_OP_THRESHOLD
            }
''',
    'stall timeout match',
)
t = replace_one(
    t,
    '''            OperationPhase::Sync | OperationPhase::Verify | OperationPhase::Commit => {
                LONG_OP_THRESHOLD.as_secs()
            }
''',
    '''            OperationPhase::Sync | OperationPhase::Commit => {
                LONG_OP_THRESHOLD.as_secs()
            }
''',
    'stall limit match',
)
p.write_text(t, encoding='utf-8')

p = Path('src/engine_impl/part4.rs')
t = p.read_text(encoding='utf-8')
t = t.replace(
    '(OperationPhase::Verify, Instant::now() - LONG_OP_THRESHOLD - Duration::from_secs(1))',
    '(OperationPhase::Sync, Instant::now() - LONG_OP_THRESHOLD - Duration::from_secs(1))',
)
t = replace_one(
    t,
    '''        control.enter_operation(OperationPhase::Sync);
        assert_eq!(control.stall_limit_secs(), 120);
        control.enter_operation(OperationPhase::Verify);
        assert_eq!(control.stall_limit_secs(), 120);
        control.enter_operation(OperationPhase::Commit);
''',
    '''        control.enter_operation(OperationPhase::Sync);
        assert_eq!(control.stall_limit_secs(), 120);
        control.enter_operation(OperationPhase::Commit);
''',
    'remove obsolete Verify watchdog assertion',
)
p.write_text(t, encoding='utf-8')

print('dead verification phase removed')
