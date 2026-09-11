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
    fn accent_foreground_keeps_readable_contrast() {
        let previous = SYSTEM_ACCENT_RGB.load(Ordering::Relaxed);
        SYSTEM_ACCENT_RGB.store(0x00FF_FFFF, Ordering::Relaxed);
        assert_eq!(Theme::on_accent(true), Color32::from_rgb(18, 18, 18));
        SYSTEM_ACCENT_RGB.store(0x0000_0000, Ordering::Relaxed);
        assert_eq!(Theme::on_accent(false), Color32::WHITE);
        SYSTEM_ACCENT_RGB.store(previous, Ordering::Relaxed);
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
        let mut app = CopierApp::new();
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
        assert!(app.dests.iter().any(|path| CopierApp::same_path(path, r"G:\")));
        assert!(app.dests.iter().any(|path| CopierApp::same_path(path, r"H:\")));
    }

    #[cfg(windows)]
    #[test]
    fn windows_destination_keys_are_case_insensitive() {
        assert!(CopierApp::same_path(r"F:\Backups\", r"f:/backups"));
    }

    #[cfg(windows)]
    #[test]
    fn windows_destination_keys_normalize_mixed_separators_and_trailing_slashes() {
        assert!(CopierApp::same_path(
            r"F:/Backups/Nested/",
            r"f:\backups\nested\\"
        ));
        assert!(CopierApp::same_path(
            r"\\Server\Share\Folder\",
            r"//server/share/folder/"
        ));
    }

    #[cfg(windows)]
    #[test]
    fn destination_batch_rejects_equivalent_windows_paths() {
        let mut app = CopierApp::new();
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