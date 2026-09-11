#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Barrier, Condvar};

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let p = std::env::temp_dir().join(format!("disk-duplicator-preflight-{name}-{stamp}"));
        fs::create_dir_all(&p).unwrap();
        p
    }

    fn opts(skip_same: bool) -> CopyOpts {
        CopyOpts { verify: true, skip_same, keep_going: true }
    }

    #[test]
    fn scan_never_filters_regular_files_by_name() {
        let root = temp_dir("all-files");
        fs::write(root.join("normal.bin"), b"1").unwrap();
        fs::write(root.join("legit.part"), b"2").unwrap();
        fs::write(root.join("paquetecopies.b3"), b"3").unwrap();
        fs::create_dir_all(root.join(".disk-duplicator")).unwrap();
        fs::write(root.join(".disk-duplicator/completed.jsonl"), b"4").unwrap();
        let (files, dirs) = scan_source(&root).unwrap();
        assert_eq!(files.len(), 4);
        assert!(files.iter().any(|f| f.rel == Path::new("legit.part")));
        assert!(files.iter().any(|f| f.rel == Path::new("paquetecopies.b3")));
        assert!(dirs.contains(&PathBuf::from(".disk-duplicator")));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn empty_folders_are_in_plan() {
        let root = temp_dir("empty-dir");
        fs::create_dir_all(root.join("a/b/empty")).unwrap();
        let (files, dirs) = scan_source(&root).unwrap();
        assert!(files.is_empty());
        assert!(dirs.contains(&PathBuf::from("a/b/empty")));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn structural_validation_detects_missing_file() {
        let root = temp_dir("missing");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"abc").unwrap();
        let (files, dirs) = scan_source(&source).unwrap();
        assert!(validate_destination_result(&source, &dest, &files, &dirs, false).is_err());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn state_location_is_not_inside_copy() {
        let root = temp_dir("state");
        let dest = root.join("Copy");
        fs::create_dir_all(&dest).unwrap();
        assert!(!state_dir_for(&dest).starts_with(&dest));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn round_up_tracks_allocation_units() {
        assert_eq!(round_up(0, 4096), 0);
        assert_eq!(round_up(1, 4096), 4096);
        assert_eq!(round_up(4097, 4096), 8192);
    }

    #[test]
    fn reserve_is_bounded() {
        assert_eq!(reserve_for_volume(10 * 1024 * 1024 * 1024), MIN_FREE_RESERVE);
        assert_eq!(reserve_for_volume(100 * 1024 * 1024 * 1024 * 1024), MAX_FREE_RESERVE);
    }

    #[test]
    fn completed_state_requires_manifest_proof() {
        let root = temp_dir("resume-proof");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"abc").unwrap();
        fs::copy(source.join("a.bin"), dest.join("a.bin")).unwrap();
        let src_mtime = fs::metadata(source.join("a.bin")).unwrap().modified().unwrap();
        File::options().write(true).open(dest.join("a.bin")).unwrap().set_modified(src_mtime).unwrap();

        let (files, _) = scan_source(&source).unwrap();
        let key = state_key(&files[0]);
        let dir = state_dir_for(&dest);
        fs::create_dir_all(&dir).unwrap();
        fs::write(state_path(&dest), format!("{{\"key\":\"{key}\"}}\n")).unwrap();

        let valid = normalize_completed_state(&source, &dest, &files).unwrap();
        assert!(valid.is_empty(), "journal sin hash durable no puede conservar estado completado");
        assert!(load_completed(&dest).is_empty());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn malformed_manifest_entry_invalidates_resume() {
        let root = temp_dir("resume-malformed-manifest");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"abc").unwrap();
        fs::copy(source.join("a.bin"), dest.join("a.bin")).unwrap();
        let (files, _) = scan_source(&source).unwrap();
        let key = state_key(&files[0]);
        let dir = state_dir_for(&dest);
        fs::create_dir_all(&dir).unwrap();
        fs::write(state_path(&dest), format!("{{\"key\":\"{key}\"}}\n")).unwrap();
        fs::write(manifest_path(&dest), "not-a-hash  a.bin\n").unwrap();

        let valid = normalize_completed_state(&source, &dest, &files).unwrap();
        assert!(valid.is_empty());
        assert!(load_completed(&dest).is_empty());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn manifest_compaction_keeps_only_latest_current_entry() {
        let root = temp_dir("manifest-compact");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"new").unwrap();
        let (files, _) = scan_source(&source).unwrap();
        let dir = state_dir_for(&dest);
        fs::create_dir_all(&dir).unwrap();
        let old_hash = blake3::hash(b"old");
        let new_hash = blake3::hash(b"new");
        fs::write(
            manifest_path(&dest),
            format!(
                "{}  a.bin\n{}  stale.bin\n{}  a.bin\n",
                old_hash.to_hex(), old_hash.to_hex(), new_hash.to_hex()
            ),
        ).unwrap();

        compact_manifest(&dest, &files).unwrap();
        let text = fs::read_to_string(manifest_path(&dest)).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 1);
        assert_eq!(lines[0], format!("{}  a.bin", new_hash.to_hex()));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn manifest_rejects_same_size_corruption_from_resume_state() {
        let root = temp_dir("resume-hash");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"abc").unwrap();
        fs::write(dest.join("a.bin"), b"xyz").unwrap();

        let (files, _) = scan_source(&source).unwrap();
        let key = state_key(&files[0]);
        let state_dir = state_dir_for(&dest);
        fs::create_dir_all(&state_dir).unwrap();
        fs::write(state_path(&dest), format!("{{\"key\":\"{key}\"}}\n")).unwrap();
        fs::write(
            manifest_path(&dest),
            format!("{}  a.bin\n", blake3::hash(b"abc").to_hex()),
        ).unwrap();

        let valid = normalize_completed_state(&source, &dest, &files).unwrap();
        assert!(valid.is_empty(), "un archivo corrupto no puede conservar estado completado");
        assert!(load_completed(&dest).is_empty(), "el journal debe limpiarse tras detectar corrupción");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn manifest_rejects_resume_when_source_diverges_from_recorded_hash() {
        let root = temp_dir("resume-source-change");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"abc").unwrap();
        fs::write(dest.join("a.bin"), b"abc").unwrap();

        let (files, _) = scan_source(&source).unwrap();
        let key = state_key(&files[0]);
        let state_dir = state_dir_for(&dest);
        fs::create_dir_all(&state_dir).unwrap();
        fs::write(state_path(&dest), format!("{{\"key\":\"{key}\"}}\n")).unwrap();
        fs::write(
            manifest_path(&dest),
            format!("{}  a.bin\n", blake3::hash(b"abc").to_hex()),
        ).unwrap();

        fs::write(source.join("a.bin"), b"xyz").unwrap();

        let valid = normalize_completed_state(&source, &dest, &files).unwrap();
        assert!(valid.is_empty(), "el journal no puede aceptar un origen distinto al hash persistido");
        assert!(load_completed(&dest).is_empty(), "el estado inválido debe eliminarse del journal");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn backup_is_restored_after_interrupted_commit() {
        let root = temp_dir("backup-recovery");
        let dest = root.join("dst");
        fs::create_dir_all(&dest).unwrap();
        prepare_state_dir(&dest).unwrap();
        let info = FileInfo { rel: PathBuf::from("a.bin"), size: 3, mtime_ns: 0 };
        let dst = dest.join(&info.rel);
        fs::write(&dst, b"old").unwrap();
        let backup = backup_path(&dest, &dst);
        fs::create_dir_all(backup.parent().unwrap()).unwrap();
        fs::rename(&dst, &backup).unwrap();

        cleanup_owned_stale_files(&dest, std::slice::from_ref(&info)).unwrap();
        assert_eq!(fs::read(&dst).unwrap(), b"old");
        assert!(!backup.exists());
        let _ = fs::remove_dir_all(root);
    }

    #[cfg(windows)]
    #[test]
    fn previous_32_hex_backup_is_restored_after_id_upgrade() {
        let root = temp_dir("previous-backup-recovery");
        let dest = root.join("DstWithUPPERCase");
        fs::create_dir_all(&dest).unwrap();
        prepare_state_dir(&dest).unwrap();
        let info = FileInfo { rel: PathBuf::from("FileWithUPPERCase.bin"), size: 3, mtime_ns: 0 };
        let dst = dest.join(&info.rel);
        let backup = previous_backup_path(&dest, &dst);
        let current_backup = backup_path(&dest, &dst);
        assert_ne!(backup, current_backup, "la prueba necesita IDs transitorios distintos");
        fs::create_dir_all(backup.parent().unwrap()).unwrap();
        fs::write(&backup, b"old").unwrap();

        cleanup_owned_stale_files(&dest, std::slice::from_ref(&info)).unwrap();
        assert_eq!(fs::read(&dst).unwrap(), b"old");
        assert!(!backup.exists());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn final_hash_detects_source_change_with_preserved_metadata() {
        let root = temp_dir("source-content-change");
        let source = root.join("src");
        fs::create_dir_all(&source).unwrap();
        let path = source.join("a.bin");
        fs::write(&path, b"abc").unwrap();
        let original_mtime = fs::metadata(&path).unwrap().modified().unwrap();
        let (files, _) = scan_source(&source).unwrap();
        let mut reader_hashes = std::collections::HashMap::new();
        reader_hashes.insert(PathBuf::from("a.bin"), *blake3::hash(b"abc").as_bytes());

        fs::write(&path, b"xyz").unwrap();
        File::options().write(true).open(&path).unwrap().set_modified(original_mtime).unwrap();

        assert!(final_source_hashes(&source, &files, &reader_hashes).is_err());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn supervisor_waits_for_worker_termination_after_cancel() {
        let root = temp_dir("supervisor-cancel");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();

        let state = Arc::new(JobState {
            running: std::sync::atomic::AtomicBool::new(true),
            cancel: std::sync::atomic::AtomicBool::new(true),
            pause: std::sync::atomic::AtomicBool::new(false),
            pause_mutex: std::sync::Mutex::new(()),
            pause_cv: Condvar::new(),
            files_total: std::sync::atomic::AtomicU64::new(0),
            bytes_total: std::sync::atomic::AtomicU64::new(0),
            buffers_in_flight: Arc::new(std::sync::atomic::AtomicUsize::new(0)),
            max_buffers: 1,
            dests: std::sync::Mutex::new(vec![crate::engine_impl::DestProgress {
                label: dest.display().to_string(),
                written: 0,
                total: 0,
                files_done: 0,
                files_skip: 0,
                files_err: 0,
                bps: 0.0,
                bps_recent: 0.0,
                last_tick: std::time::Instant::now(),
                phase: DestPhase::Copying,
                error: None,
                last_file: String::new(),
                queue_depth: 0,
                retries: 0,
            }]),
            reader_hashes: std::sync::Mutex::new(std::collections::HashMap::new()),
        });

        let barrier = Arc::new(Barrier::new(2));
        let worker_barrier = Arc::clone(&barrier);
        let worker = thread::spawn(move || {
            worker_barrier.wait();
        });
        let supervisor = supervise_job(
            source,
            vec![dest],
            Arc::new(Vec::new()),
            Arc::new(Vec::new()),
            Arc::clone(&state),
            vec![worker],
            opts(false),
        );

        thread::sleep(Duration::from_millis(60));
        assert!(state.running.load(Ordering::Acquire), "el supervisor se desacopló del worker cancelado");
        barrier.wait();
        supervisor.join().unwrap();
        assert!(!state.running.load(Ordering::Acquire));
        assert_eq!(state.snapshot()[0].phase, DestPhase::Cancelled);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn final_verify_uses_manifest_hash_and_rejects_corruption() {
        let root = temp_dir("final-hash");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"abc").unwrap();
        fs::write(dest.join("a.bin"), b"xyz").unwrap();

        let (files, dirs) = scan_source(&source).unwrap();
        let state_dir = state_dir_for(&dest);
        fs::create_dir_all(&state_dir).unwrap();
        fs::write(
            manifest_path(&dest),
            format!("{}  a.bin\n", blake3::hash(b"abc").to_hex()),
        ).unwrap();

        let err = validate_destination_result(&source, &dest, &files, &dirs, true).unwrap_err();
        assert!(err.contains("BLAKE3 final no coincide"));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn opts_helper_compiles() {
        assert!(opts(true).skip_same);
        assert!(opts(false).verify);
    }
}