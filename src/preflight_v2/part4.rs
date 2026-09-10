#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let p = std::env::temp_dir().join(format!("disk-duplicator-preflight-{name}-{stamp}"));
        fs::create_dir_all(&p).unwrap();
        p
    }

    fn opts(skip_same: bool) -> CopyOpts {
        CopyOpts { verify: false, skip_same, keep_going: true }
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
    fn completed_state_requires_physical_match() {
        let root = temp_dir("resume");
        let source = root.join("src");
        let dest = root.join("dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(source.join("a.bin"), b"abc").unwrap();
        let (files, _) = scan_source(&source).unwrap();
        let key = state_key(&files[0]);
        let dir = state_dir_for(&dest);
        fs::create_dir_all(&dir).unwrap();
        fs::write(state_path(&dest), format!("{{\"key\":\"{key}\"}}\n")).unwrap();
        let valid = normalize_completed_state(&source, &dest, &files).unwrap();
        assert!(valid.is_empty());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn opts_helper_compiles() {
        assert!(opts(true).skip_same);
    }
}
