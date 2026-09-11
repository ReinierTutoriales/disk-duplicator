from pathlib import Path

p=Path('src/preflight/part1.rs')
s=p.read_text(encoding='utf-8')

needle='''fn recover_completed_rewrite(dest: &Path) -> Result<(), String> {\n'''
insert='''fn ensure_owned_regular_file(path: &Path, label: &str) -> Result<(), String> {\n    let meta = fs::symlink_metadata(path)\n        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;\n    if meta.file_type().is_symlink() || is_reparse_point(&meta) || !meta.is_file() {\n        return Err(format!(\n            "Entrada de estado no segura para {label}: {}. Se esperaba un archivo regular sin enlaces ni reparse points.",\n            path.display()\n        ));\n    }\n    Ok(())\n}\n\nfn recover_completed_rewrite(dest: &Path) -> Result<(), String> {\n'''
assert needle in s
s=s.replace(needle,insert,1)

s=s.replace('''    if path.exists() {\n        if backup.exists() {\n            fs::remove_file(&backup).map_err(|e| {''','''    if path.exists() {\n        ensure_owned_regular_file(&path, "journal")?;\n        if backup.exists() {\n            ensure_owned_regular_file(&backup, "backup de estado")?;\n            fs::remove_file(&backup).map_err(|e| {''',1)
s=s.replace('''        if tmp.exists() {\n            fs::remove_file(&tmp).map_err(|e| {''','''        if tmp.exists() {\n            ensure_owned_regular_file(&tmp, "temporal de estado")?;\n            fs::remove_file(&tmp).map_err(|e| {''',1)
s=s.replace('''    if backup.exists() {\n        fs::rename(&backup, &path)''','''    if backup.exists() {\n        ensure_owned_regular_file(&backup, "backup de estado")?;\n        fs::rename(&backup, &path)''',1)
s=s.replace('''    if tmp.exists() {\n        fs::remove_file(&tmp)''','''    if tmp.exists() {\n        ensure_owned_regular_file(&tmp, "temporal de estado")?;\n        fs::remove_file(&tmp)''',1)

s=s.replace('''            if !backup.exists() {\n                continue;\n            }\n            if dst.exists() {''','''            if !backup.exists() {\n                continue;\n            }\n            ensure_owned_regular_file(&backup, "backup de copia")?;\n            if dst.exists() {''',1)

p.write_text(s,encoding='utf-8')

p=Path('src/engine_impl/part2.rs')
s=p.read_text(encoding='utf-8')
needle='''    let errs = state.dests.lock().unwrap()[slot].files_err;\n'''
replace='''    control.queue_depth.store(0, Ordering::Release);\n    state.dests.lock().unwrap()[slot].queue_depth = 0;\n\n    let errs = state.dests.lock().unwrap()[slot].files_err;\n'''
assert needle in s
s=s.replace(needle,replace,1)
# Also make early finish/cancel terminal paths report a zero queue immediately.
s=s.replace('''        control.alive.store(false, Ordering::Release);\n        control.note_progress();\n        return;\n    }\n\n    if state.cancel.load(Ordering::Acquire) {''','''        control.queue_depth.store(0, Ordering::Release);\n        state.dests.lock().unwrap()[slot].queue_depth = 0;\n        control.alive.store(false, Ordering::Release);\n        control.note_progress();\n        return;\n    }\n\n    if state.cancel.load(Ordering::Acquire) {''',1)
s=s.replace('''    if state.cancel.load(Ordering::Acquire) {\n        set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));\n        control.alive.store(false, Ordering::Release);''','''    if state.cancel.load(Ordering::Acquire) {\n        set_phase(&state, slot, DestPhase::Cancelled, Some("Cancelado".into()));\n        control.queue_depth.store(0, Ordering::Release);\n        state.dests.lock().unwrap()[slot].queue_depth = 0;\n        control.alive.store(false, Ordering::Release);''',1)
p.write_text(s,encoding='utf-8')

p=Path('src/preflight/part4.rs')
s=p.read_text(encoding='utf-8')
marker='''    #[test]\n    fn backup_is_restored_after_interrupted_commit() {\n'''
tests='''    #[test]\n    fn stale_backup_directory_is_rejected_instead_of_restored() {\n        let root = temp_dir("backup-directory-reject");\n        let dest = root.join("dst");\n        fs::create_dir_all(&dest).unwrap();\n        prepare_state_dir(&dest).unwrap();\n        let info = FileInfo { rel: PathBuf::from("a.bin"), size: 3, mtime_ns: 0 };\n        let dst = dest.join(&info.rel);\n        let backup = backup_path(&dest, &dst);\n        fs::create_dir_all(&backup).unwrap();\n\n        let err = cleanup_owned_stale_files(&dest, std::slice::from_ref(&info)).unwrap_err();\n        assert!(err.contains("Entrada de estado no segura"));\n        assert!(!dst.exists());\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn state_rewrite_backup_directory_is_rejected() {\n        let root = temp_dir("state-backup-directory-reject");\n        let dest = root.join("dst");\n        fs::create_dir_all(&dest).unwrap();\n        prepare_state_dir(&dest).unwrap();\n        let backup = state_rewrite_backup_path(&dest);\n        fs::create_dir_all(&backup).unwrap();\n\n        let err = recover_completed_rewrite(&dest).unwrap_err();\n        assert!(err.contains("Entrada de estado no segura"));\n        let _ = fs::remove_dir_all(root);\n    }\n\n'''+marker
assert marker in s
s=s.replace(marker,tests,1)
p.write_text(s,encoding='utf-8')
