#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn theme_overrides_are_not_inverted() {
        assert!(resolve_theme(ThemePreference::Light));
        assert!(!resolve_theme(ThemePreference::Dark));
    }

    #[test]
    fn theme_copy_explains_each_choice() {
        for theme in [
            ThemePreference::System,
            ThemePreference::Light,
            ThemePreference::Dark,
        ] {
            assert!(!theme_description(theme).is_empty());
        }
    }

    #[test]
    fn progress_fraction_is_bounded_and_terminal_zero_is_complete() {
        assert_eq!(progress_fraction(0, 0, DestPhase::Done), 1.0_f32);
        assert_eq!(progress_fraction(0, 0, DestPhase::Copying), 0.0_f32);
        assert_eq!(progress_fraction(50, 100, DestPhase::Copying), 0.5_f32);
        assert_eq!(progress_fraction(150, 100, DestPhase::Copying), 1.0_f32);
    }

    #[test]
    fn terminal_snapshots_end_the_visual_copy_before_engine_cleanup_finishes() {
        assert!(ui_copy_active(true, false));
        assert!(!ui_copy_active(true, true));
        assert!(!ui_copy_active(false, false));
    }

    #[test]
    fn terminal_cleanup_does_not_block_a_new_drop() {
        assert!(drop_input_locked(true, false));
        assert!(drop_input_locked(false, true));
        assert!(!drop_input_locked(false, false));

        let visual_running_during_cleanup = ui_copy_active(true, true);
        assert!(!visual_running_during_cleanup);
        assert!(!drop_input_locked(false, visual_running_during_cleanup));
    }

    #[test]
    fn only_copying_phase_reports_throughput() {
        let now = Instant::now();
        assert_eq!(visible_bps(900_000_000.0, now, DestPhase::Done, false), 0.0);
        assert_eq!(visible_bps(900_000_000.0, now, DestPhase::Verifying, false), 0.0);
        assert_eq!(visible_bps(900_000_000.0, now, DestPhase::Copying, true), 0.0);
        assert!(visible_bps(900_000_000.0, now, DestPhase::Copying, false) > 0.0);
    }

    #[test]
    fn fanout_rate_is_not_inflated_by_destination_count() {
        let logical = logical_fanout_bps([108.1, 108.7, 108.8, 108.6]);
        assert!((logical - 108.1).abs() < f64::EPSILON);
        assert_eq!(logical_fanout_bps([]), 0.0);
    }

    #[test]
    fn previous_supervisor_blocks_new_job_but_not_drop_preparation() {
        assert!(!can_start_new_job(false, true));
        assert!(can_start_new_job(false, false));
        assert!(!can_start_new_job(true, false));
        assert!(!drop_input_locked(false, false));
    }

    #[test]
    fn accent_foreground_keeps_readable_contrast() {
        let previous = SYSTEM_ACCENT_RGB.load(Ordering::Relaxed);
        SYSTEM_ACCENT_RGB.store(0x00FF_FFFF, Ordering::Relaxed);
        assert_eq!(Theme::on_accent(true), Color32::from_rgb(18, 18, 18));
        SYSTEM_ACCENT_RGB.store(0x0000_0000, Ordering::Relaxed);
        assert_eq!(Theme::on_accent(false), Color32::WHITE);
        SYSTEM_ACCENT_RGB.store(previous, Ordering::Relaxed);
    }

    #[test]
    fn fluent_metrics_keep_windows_control_density() {
        assert_eq!(FLUENT_CONTROL_HEIGHT, 32.0);
        assert_eq!(FLUENT_RADIUS_SM, 6.0);
        assert_eq!(FLUENT_RADIUS_MD, 8.0);
        assert_eq!(FLUENT_RADIUS_LG, 12.0);
    }

    #[test]
    fn source_picker_supports_file_and_folder_semantics() {
        let source_file = include_str!("part3.rs");
        assert!(source_file.contains("pick_source_dir"));
        assert!(source_file.contains("pick_source_file"));
        assert!(source_file.contains("Carpeta…"));
        assert!(source_file.contains("Archivo…"));
    }

    #[test]
    fn source_layout_stacks_at_narrow_widths() {
        assert!(source_layout_stacked(680.0));
        assert!(source_layout_stacked(719.0));
        assert!(!source_layout_stacked(720.0));
        assert!(!source_layout_stacked(960.0));
    }

    #[test]
    fn disabled_start_always_has_a_specific_reason() {
        assert_eq!(
            start_disabled_reason("", 0, 0),
            Some("Selecciona un archivo o carpeta de origen")
        );
        assert_eq!(
            start_disabled_reason(r"C:\Origen", 0, 0),
            Some("Agrega al menos un destino")
        );
        assert_eq!(
            start_disabled_reason(r"C:\Origen", 1, 2),
            Some("Corrige los problemas de ruta antes de iniciar")
        );
        assert_eq!(start_disabled_reason(r"C:\Origen", 1, 0), None);
    }

    #[test]
    fn unicode_paths_survive_display_and_compaction() {
        let path = r"C:\Música\Niño\日本語\Документы\archivo-especial.txt";
        let compact = compact_path(path, 28);
        assert!(compact.starts_with("C:"));
        assert!(compact.ends_with("especial.txt"));
        assert!(compact.contains('…'));
        assert!(!compact.contains('�'));

        let extended = r"\\?\C:\Música\Niño\日本語";
        assert_eq!(display_path(extended), r"C:\Música\Niño\日本語");
    }

    #[test]
    fn ui_sources_do_not_contain_common_mojibake_sequences() {
        for source in [
            include_str!("part1.rs"),
            include_str!("part2.rs"),
            include_str!("part3.rs"),
            include_str!("part4.rs"),
        ] {
            assert!(!contains_mojibake(source));
        }
    }

    #[test]
    fn compact_path_preserves_both_ends() {
        let value = compact_path(r"C:\very\long\folder\tree\important-file.bin", 24);
        assert!(value.starts_with("C:"));
        assert!(value.ends_with("file.bin"));
        assert!(value.contains('…'));
    }

    #[test]
    fn effective_destination_shows_selected_source_root() {
        let shown = effective_destination_label(r"C:\Input\Package", r"D:\Copies");
        assert!(shown.ends_with(r"Copies\Package"));
    }

    #[test]
    fn destination_batch_skips_source_and_duplicates() {
        let mut app = CopierApp::new_with_source(None);
        app.source = r"E:\source".to_owned();
        app.dests = vec![r"F:\".to_owned()];

        let added = app.add_destinations(vec![
            r"F:\".to_owned(),
            r"G:\".to_owned(),
            r"E:\source".to_owned(),
            r"H:\".to_owned(),
            r"G:\".to_owned(),
        ]);

        assert_eq!(added, 2);
        assert_eq!(app.dests.len(), 3);
        assert!(app.dests.iter().any(|path| same_path(path, r"G:\")));
        assert!(app.dests.iter().any(|path| same_path(path, r"H:\")));
    }

    #[cfg(windows)]
    #[test]
    fn windows_destination_keys_are_case_insensitive() {
        assert!(same_path(r"F:\Backups\", r"f:/backups"));
    }

    #[cfg(windows)]
    #[test]
    fn windows_destination_keys_normalize_mixed_separators_and_trailing_slashes() {
        assert!(same_path(
            r"F:/Backups/Nested/",
            r"f:\backups\nested\\"
        ));
        assert!(same_path(
            r"\\Server\Share\Folder\",
            r"//server/share/folder/"
        ));
    }

    #[test]
    fn saved_copy_roundtrip_preserves_copy_configuration() {
        let mut app = CopierApp::new_with_source(None);
        app.source = r"C:\Música\Proyecto".to_owned();
        app.dests = vec![r"D:\Copias".to_owned(), r"E:\Respaldo".to_owned()];
        app.skip_same = false;
        app.keep_going = false;
        let snapshot = app.copy_snapshot().unwrap();

        let mut restored = CopierApp::new_with_source(None);
        restored.apply_saved_copy(snapshot);
        assert_eq!(restored.source, r"C:\Música\Proyecto");
        assert_eq!(restored.dests.len(), 2);
        assert!(!restored.skip_same);
        assert!(!restored.keep_going);
        assert!(!restored.running_job());
        assert!(restored.startup_rx.is_none());
    }

    #[test]
    fn copy_plan_rejects_source_as_destination_and_duplicates() {
        let mut app = CopierApp::new_with_source(None);
        app.source = r"C:\Origen".to_owned();
        app.dests = vec![r"C:\Origen".to_owned()];
        assert!(app.copy_snapshot().is_err());

        app.dests = vec![r"D:\Copias".to_owned(), r"D:\Copias".to_owned()];
        assert!(app.copy_snapshot().is_err());
    }

    #[cfg(windows)]
    #[test]
    fn destination_batch_rejects_equivalent_windows_paths() {
        let mut app = CopierApp::new_with_source(None);
        app.source = r"E:\source".to_owned();

        let added = app.add_destinations(vec![
            r"F:\Backups\".to_owned(),
            r"f:/backups".to_owned(),
            r"F:/BACKUPS/".to_owned(),
        ]);

        assert_eq!(added, 1);
        assert_eq!(app.dests.len(), 1);
    }
}