use std::fs;
use std::path::PathBuf;

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
pub enum ThemePreference {
    #[default]
    System,
    Light,
    Dark,
}

impl ThemePreference {
    pub fn label(self) -> &'static str {
        match self {
            Self::System => "Sistema",
            Self::Light => "Claro",
            Self::Dark => "Oscuro",
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Self::System => "system",
            Self::Light => "light",
            Self::Dark => "dark",
        }
    }

    fn parse(value: &str) -> Self {
        match value.trim().to_ascii_lowercase().as_str() {
            "light" => Self::Light,
            "dark" => Self::Dark,
            _ => Self::System,
        }
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct AppSettings {
    pub theme: ThemePreference,
}

fn settings_path() -> Option<PathBuf> {
    let appdata = std::env::var_os("APPDATA")?;
    Some(
        PathBuf::from(appdata)
            .join("RepartoCopier")
            .join("settings.conf"),
    )
}

fn parse_settings(text: &str) -> AppSettings {
    let mut settings = AppSettings::default();
    for line in text.lines() {
        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        if key.trim().eq_ignore_ascii_case("theme") {
            settings.theme = ThemePreference::parse(value);
        }
    }
    settings
}

fn render_settings(settings: AppSettings) -> String {
    format!("theme={}\n", settings.theme.as_str())
}

pub fn load_settings() -> AppSettings {
    let Some(path) = settings_path() else {
        return AppSettings::default();
    };
    let Ok(text) = fs::read_to_string(path) else {
        return AppSettings::default();
    };
    parse_settings(&text)
}

pub fn save_settings(settings: AppSettings) -> Result<(), String> {
    let path = settings_path().ok_or_else(|| {
        "Windows no proporcionó una carpeta APPDATA para guardar los ajustes.".to_owned()
    })?;
    let parent = path
        .parent()
        .ok_or_else(|| "Ruta de configuración inválida.".to_owned())?;
    fs::create_dir_all(parent)
        .map_err(|e| format!("No se pudo crear la carpeta de ajustes: {e}"))?;

    let tmp = path.with_extension("conf.tmp");
    fs::write(&tmp, render_settings(settings))
        .map_err(|e| format!("No se pudieron guardar los ajustes: {e}"))?;
    fs::rename(&tmp, &path).or_else(|rename_err| {
        if path.exists() {
            fs::remove_file(&path).map_err(|remove_err| {
                format!(
                    "No se pudo reemplazar la configuración ({rename_err}); tampoco se pudo retirar la anterior ({remove_err})."
                )
            })?;
            fs::rename(&tmp, &path)
        } else {
            Err(rename_err)
        }
    })
    .map_err(|e| format!("No se pudo finalizar el guardado de ajustes: {e}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn theme_settings_roundtrip() {
        for theme in [
            ThemePreference::System,
            ThemePreference::Light,
            ThemePreference::Dark,
        ] {
            let text = render_settings(AppSettings { theme });
            assert_eq!(parse_settings(&text).theme, theme);
        }
    }

    #[test]
    fn corrupt_or_unknown_theme_falls_back_to_system() {
        assert_eq!(parse_settings("theme=neon\n").theme, ThemePreference::System);
        assert_eq!(parse_settings("garbage\n").theme, ThemePreference::System);
    }
}
