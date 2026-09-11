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
windows_path = Path('src/windows_io.rs')

paths = paths_path.read_text(encoding='utf-8')
part1 = engine1_path.read_text(encoding='utf-8')
part2 = engine2_path.read_text(encoding='utf-8')
part4 = engine4_path.read_text(encoding='utf-8')
windows = windows_path.read_text(encoding='utf-8')

anchor = '''pub(crate) fn state_path(dest: &Path) -> PathBuf {\n    state_dir_for(dest).join("completed.jsonl")\n}\n'''
insert = '''pub(crate) fn prepare_runtime_state_tmp(dest: &Path) -> Result<PathBuf, String> {\n    let current = prepare_state_dir(dest)?;\n    let tmp = current.join("tmp");\n    create_normal_dir_if_missing(&tmp, "El directorio temporal de estado")?;\n    Ok(tmp)\n}\n\npub(crate) fn state_path(dest: &Path) -> PathBuf {\n    state_dir_for(dest).join("completed.jsonl")\n}\n'''
paths = one(paths, anchor, insert, 'runtime state tmp helper')

ensure_anchor = '''fn ensure_normal_dir(path: &Path, label: &str) -> Result<(), String> {\n    let meta = std::fs::symlink_metadata(path)\n        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;\n    if !meta.is_dir() || meta.file_type().is_symlink() || is_reparse_point(&meta) {\n        return Err(format!(\n            "{label} no es un directorio normal o es un enlace/junction/reparse point: {}.",\n            path.display()\n        ));\n    }\n    Ok(())\n}\n'''
ensure_insert = ensure_anchor + '''\nfn create_normal_dir_if_missing(path: &Path, label: &str) -> Result<(), String> {\n    match std::fs::create_dir(path) {\n        Ok(()) => {}\n        Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {}\n        Err(e) => {\n            return Err(format!("No se pudo crear {label} {}: {e}", path.display()));\n        }\n    }\n    ensure_normal_dir(path, label)\n}\n'''
paths = one(paths, ensure_anchor, ensure_insert, 'race-safe directory helper')

paths = one(
    paths,
    '''    if container.exists() {\n        ensure_normal_dir(container, "El contenedor de estado")?;\n    } else {\n        std::fs::create_dir(container).map_err(|e| {\n            format!(\n                "No se pudo crear el contenedor de estado {}: {e}",\n                container.display()\n            )\n        })?;\n        ensure_normal_dir(container, "El contenedor de estado")?;\n    }\n''',
    '''    create_normal_dir_if_missing(container, "El contenedor de estado")?;\n''',
    'race-safe state container creation',
)

paths = one(
    paths,
    '''    if !current.exists() {\n        std::fs::create_dir(&current).map_err(|e| {\n            format!(\n                "No se pudo crear el directorio de estado {}: {e}",\n                current.display()\n            )\n        })?;\n    }\n    ensure_normal_dir(&current, "El directorio de estado")?;\n''',
    '''    create_normal_dir_if_missing(&current, "El directorio de estado")?;\n''',
    'race-safe state directory creation',
)

old_import = '''use crate::paths::{backup_path, manifest_path, part_path, persisted_path_key, state_dir_for, state_path};'''
new_import = '''use crate::paths::{\n    backup_path, manifest_path, part_path, persisted_path_key, prepare_runtime_state_tmp,\n    prepare_state_dir, state_path,\n};\n#[cfg(test)]\nuse crate::paths::state_dir_for;'''
part1 = one(part1, old_import, new_import, 'engine paths import')

part1 = one(
    part1,
    '''fn validate_source_snapshot(path: &Path, info: &FileInfo) -> Result<(), String> {\n    let meta = fs::metadata(path).map_err(|e| format!("origen {}: {e}", path.display()))?;\n    if !meta.is_file() || meta.len() != info.size || metadata_mtime_ns(&meta) != info.mtime_ns {\n        return Err(format!("origen cambió: {}", path.display()));\n    }\n    Ok(())\n}\n\nfn create_directory_layout(dests: &[PathBuf], dirs: &[PathBuf]) -> Result<(), String> {\n    for dest in dests {\n        fs::create_dir_all(dest).map_err(|e| format!("destino {}: {e}", dest.display()))?;\n        for rel in dirs {\n            let path = dest.join(rel);\n            fs::create_dir_all(&path)\n                .map_err(|e| format!("No se pudo crear la carpeta {}: {e}", path.display()))?;\n        }\n    }\n    Ok(())\n}\n''',
    '''fn validate_source_snapshot(path: &Path, info: &FileInfo) -> Result<(), String> {\n    let meta = fs::symlink_metadata(path).map_err(|e| format!("origen {}: {e}", path.display()))?;\n    if meta.file_type().is_symlink()\n        || runtime_is_reparse(&meta)\n        || !meta.is_file()\n        || meta.len() != info.size\n        || metadata_mtime_ns(&meta) != info.mtime_ns\n    {\n        return Err(format!("origen cambió: {}", path.display()));\n    }\n    Ok(())\n}\n\nfn ensure_runtime_destination_directory(root: &Path, rel: &Path) -> Result<(), String> {\n    let root_meta = fs::symlink_metadata(root)\n        .map_err(|e| format!("No se pudo inspeccionar destino {}: {e}", root.display()))?;\n    if !root_meta.is_dir() || root_meta.file_type().is_symlink() || runtime_is_reparse(&root_meta) {\n        return Err(format!("Destino inseguro o reemplazado durante la copia: {}", root.display()));\n    }\n\n    let mut current = root.to_path_buf();\n    let mut saw_component = false;\n    for component in rel.components() {\n        let Component::Normal(name) = component else {\n            return Err(format!("Ruta relativa insegura: {}", rel.display()));\n        };\n        saw_component = true;\n        current.push(name);\n        match fs::symlink_metadata(&current) {\n            Ok(meta) => {\n                if !meta.is_dir() || meta.file_type().is_symlink() || runtime_is_reparse(&meta) {\n                    return Err(format!("Componente de carpeta inseguro en destino: {}", current.display()));\n                }\n            }\n            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {\n                match fs::create_dir(&current) {\n                    Ok(()) => {}\n                    Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {}\n                    Err(e) => return Err(format!("No se pudo crear la carpeta {}: {e}", current.display())),\n                }\n                let meta = fs::symlink_metadata(&current)\n                    .map_err(|e| format!("No se pudo verificar la carpeta {}: {e}", current.display()))?;\n                if !meta.is_dir() || meta.file_type().is_symlink() || runtime_is_reparse(&meta) {\n                    return Err(format!("La carpeta creada no es segura: {}", current.display()));\n                }\n            }\n            Err(e) => return Err(format!("No se pudo inspeccionar {}: {e}", current.display())),\n        }\n    }\n    if !saw_component {\n        return Err("Ruta relativa vacía en el layout de carpetas".into());\n    }\n    Ok(())\n}\n\nfn create_directory_layout(dests: &[PathBuf], dirs: &[PathBuf]) -> Result<(), String> {\n    for dest in dests {\n        fs::create_dir_all(dest).map_err(|e| format!("destino {}: {e}", dest.display()))?;\n        let meta = fs::symlink_metadata(dest)\n            .map_err(|e| format!("No se pudo inspeccionar destino {}: {e}", dest.display()))?;\n        if !meta.is_dir() || meta.file_type().is_symlink() || runtime_is_reparse(&meta) {\n            return Err(format!("Destino inseguro o reemplazado durante la copia: {}", dest.display()));\n        }\n        for rel in dirs {\n            ensure_runtime_destination_directory(dest, rel)?;\n        }\n    }\n    Ok(())\n}\n''',
    'runtime source and directory layout',
)

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

windows = one(
    windows,
    '''                    _ => return Err(io::Error::last_os_error()),\n''',
    '''                    _ => {\n                        let err = io::Error::last_os_error();\n                        unsafe {\n                            CancelIoEx(self.handle, &overlapped);\n                            WaitForSingleObject(self.event, INFINITE);\n                        }\n                        return Err(err);\n                    }\n''',
    'writer wait drain',
)

needle = '''    #[test]\n    fn state_is_outside_destination_tree() {'''
test = '''    #[test]\n    fn runtime_state_tmp_rejects_non_directory_entry() {\n        let root = temp_dir("runtime-state-tmp");\n        let dest = root.join("CopyName");\n        fs::create_dir_all(&dest).unwrap();\n        let state = prepare_state_dir(&dest).unwrap();\n        fs::write(state.join("tmp"), b"not-a-directory").unwrap();\n        assert!(prepare_runtime_state_tmp(&dest).is_err());\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn runtime_state_creation_is_safe_for_sibling_destinations() {\n        let root = temp_dir("runtime-state-race");\n        let mut workers = Vec::new();\n        for index in 0..8 {\n            let dest = root.join(format!("dest-{index}"));\n            fs::create_dir_all(&dest).unwrap();\n            workers.push(thread::spawn(move || prepare_runtime_state_tmp(&dest)));\n        }\n        for worker in workers {\n            let tmp = worker.join().unwrap().unwrap();\n            assert!(tmp.is_dir());\n        }\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn directory_layout_rejects_file_as_intermediate_component() {\n        let root = temp_dir("unsafe-dir-layout");\n        let dest = root.join("dest");\n        fs::create_dir_all(&dest).unwrap();\n        fs::write(dest.join("a"), b"file").unwrap();\n        let dirs = vec![PathBuf::from("a/b")];\n        assert!(create_directory_layout(std::slice::from_ref(&dest), &dirs).is_err());\n        let _ = fs::remove_dir_all(root);\n    }\n\n    #[test]\n    fn state_is_outside_destination_tree() {'''
part4 = one(part4, needle, test, 'runtime state and directory regressions')

paths_path.write_text(paths, encoding='utf-8')
engine1_path.write_text(part1, encoding='utf-8')
engine2_path.write_text(part2, encoding='utf-8')
engine4_path.write_text(part4, encoding='utf-8')
windows_path.write_text(windows, encoding='utf-8')