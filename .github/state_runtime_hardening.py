from pathlib import Path


def one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

paths_path = Path('src/paths.rs')
engine1_path = Path('src/engine_impl/part1.rs')
engine2_path = Path('src/engine_impl/part2.rs')
engine4_path = Path('src/engine_impl/part4.rs')

paths = paths_path.read_text(encoding='utf-8')
part1 = engine1_path.read_text(encoding='utf-8')
part2 = engine2_path.read_text(encoding='utf-8')
part4 = engine4_path.read_text(encoding='utf-8')

anchor = '''pub(crate) fn state_path(dest: &Path) -> PathBuf {\n    state_dir_for(dest).join("completed.jsonl")\n}\n'''
insert = '''pub(crate) fn prepare_runtime_state_tmp(dest: &Path) -> Result<PathBuf, String> {\n    let current = prepare_state_dir(dest)?;\n    let tmp = current.join("tmp");\n    if tmp.exists() {\n        ensure_normal_dir(&tmp, "El directorio temporal de estado")?;\n    } else {\n        std::fs::create_dir(&tmp).map_err(|e| {\n            format!("No se pudo crear el directorio temporal de estado {}: {e}", tmp.display())\n        })?;\n        ensure_normal_dir(&tmp, "El directorio temporal de estado")?;\n    }\n    Ok(tmp)\n}\n\npub(crate) fn state_path(dest: &Path) -> PathBuf {\n    state_dir_for(dest).join("completed.jsonl")\n}\n'''
paths = one(paths, anchor, insert, 'runtime state tmp helper')

old_import = '''use crate::paths::{backup_path, manifest_path, part_path, persisted_path_key, state_dir_for, state_path};'''
new_import = '''use crate::paths::{\n    backup_path, manifest_path, part_path, persisted_path_key, prepare_runtime_state_tmp,\n    prepare_state_dir, state_dir_for, state_path,\n};'''
part1 = one(part1, old_import, new_import, 'engine paths import')

part1 = one(
    part1,
    '''        let dir = state_dir_for(dest);\n        fs::create_dir_all(&dir).map_err(|e| format!("state mkdir {}: {e}", dir.display()))?;\n        let file = OpenOptions::new().create(true).append(true).open(state_path(dest))\n''',
    '''        prepare_state_dir(dest)?;\n        let file = OpenOptions::new().create(true).append(true).open(state_path(dest))\n''',
    'journal runtime state validation',
)

part1 = one(
    part1,
    '''        let dir = state_dir_for(dest);\n        fs::create_dir_all(&dir).map_err(|e| format!("manifest mkdir {}: {e}", dir.display()))?;\n        let file = OpenOptions::new().create(true).append(true).open(manifest_path(dest))\n''',
    '''        prepare_state_dir(dest)?;\n        let file = OpenOptions::new().create(true).append(true).open(manifest_path(dest))\n''',
    'manifest runtime state validation',
)

part2 = one(
    part2,
    '''    let tmp_dir = state_dir_for(&dest).join("tmp");\n    if let Err(e) = fs::create_dir_all(&tmp_dir) {\n''',
    '''    let tmp_dir = match prepare_runtime_state_tmp(&dest) {\n        Ok(path) => path,\n        Err(e) => {\n            control.alive.store(false, Ordering::Release);\n            set_phase(&state, slot, DestPhase::Failed, Some(e));\n            return;\n        }\n    };\n    if !tmp_dir.is_dir() {\n''',
    'worker tmp startup',
)
part2 = one(
    part2,
    '''        control.alive.store(false, Ordering::Release);\n        set_phase(\n            &state,\n            slot,\n            DestPhase::Failed,\n            Some(format!("tmp mkdir {}: {e}", tmp_dir.display())),\n        );\n        return;\n    }\n\n    let mut verify_buf''',
    '''        control.alive.store(false, Ordering::Release);\n        set_phase(\n            &state,\n            slot,\n            DestPhase::Failed,\n            Some(format!("El temporal de estado no está disponible: {}", tmp_dir.display())),\n        );\n        return;\n    }\n\n    let mut verify_buf''',
    'worker tmp startup error',
)

part2 = one(
    part2,
    '''                let dst = dest.join(&info.rel);\n                if let Err(e) = validate_runtime_destination_path(&dest, &info.rel) {\n''',
    '''                let dst = dest.join(&info.rel);\n                if let Err(e) = prepare_runtime_state_tmp(&dest) {\n                    record_file_error(&state, slot, e);\n                    control.alive.store(false, Ordering::Release);\n                    break;\n                }\n                if let Err(e) = validate_runtime_destination_path(&dest, &info.rel) {\n''',
    'begin state path validation',
)

part2 = one(
    part2,
    '''                if let Err(e) = validate_runtime_destination_path(&dest, &cur.info.rel) {\n                    cleanup_part(&dest, &dst);\n''',
    '''                if let Err(e) = prepare_runtime_state_tmp(&dest) {\n                    cleanup_part(&dest, &dst);\n                    rollback_write_progress(&state, slot, cur.copied, &mut effective_written, start);\n                    record_file_error(&state, slot, e);\n                    control.alive.store(false, Ordering::Release);\n                    break;\n                }\n                if let Err(e) = validate_runtime_destination_path(&dest, &cur.info.rel) {\n                    cleanup_part(&dest, &dst);\n''',
    'commit state path validation',
)

needle = '''    #[test]\n    fn state_is_outside_destination_tree() {'''
test = '''    #[test]\n    fn runtime_state_tmp_rejects_non_directory_entry() {\n        let root = temp_dir("runtime-state-tmp");\n        let dest = root.join("CopyName");\n        fs::create_dir_all(&dest).unwrap();\n        let state = prepare_state_dir(&dest).unwrap();\n        fs::write(state.join("tmp"), b"not-a-directory").unwrap();\n        assert!(prepare_runtime_state_tmp(&dest).is_err());\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn state_is_outside_destination_tree() {'''
part4 = one(part4, needle, test, 'runtime state tmp regression')

paths_path.write_text(paths, encoding='utf-8')
engine1_path.write_text(part1, encoding='utf-8')
engine2_path.write_text(part2, encoding='utf-8')
engine4_path.write_text(part4, encoding='utf-8')
