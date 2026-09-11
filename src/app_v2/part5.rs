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
}
