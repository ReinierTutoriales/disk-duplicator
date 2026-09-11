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
}
