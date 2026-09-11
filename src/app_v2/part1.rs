use crate::config::{load_settings, save_settings, AppSettings, ThemePreference};
use crate::engine::{format_bps, start_job, CopyOpts, DestPhase, JobState};
use eframe::egui::{self, Color32, RichText};
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

type StartResult = Result<(Arc<JobState>, Vec<JoinHandle<()>>), String>;

const SPACING_XS: f32 = 4.0;
const SPACING_SM: f32 = 8.0;
const SPACING_MD: f32 = 12.0;
const SPACING_LG: f32 = 18.0;
const RUNNING_REPAINT: Duration = Duration::from_millis(200);
const PAUSED_REPAINT: Duration = Duration::from_millis(500);
const STARTING_REPAINT: Duration = Duration::from_millis(80);
const ERROR_FLASH: Duration = Duration::from_secs(5);
const THEME_CHECK_INTERVAL: Duration = Duration::from_secs(30);
const PATH_CHECK_INTERVAL: Duration = Duration::from_secs(2);
const SPEED_DECAY_GRACE_SECS: f64 = 0.5;
const SPEED_DECAY_TAU_SECS: f64 = 2.0;

#[cfg(windows)]
mod system_theme {
    use std::ffi::c_void;

    type HKey = *mut c_void;
    const HKEY_CURRENT_USER: HKey = 0x8000_0001usize as HKey;
    const KEY_READ: u32 = 0x0002_0019;
    const REG_DWORD: u32 = 4;

    #[link(name = "advapi32")]
    extern "system" {
        #[link_name = "RegOpenKeyExW"]
        fn reg_open_key_ex_w(
            hkey: HKey,
            sub_key: *const u16,
            options: u32,
            desired: u32,
            result: *mut HKey,
        ) -> i32;
        #[link_name = "RegQueryValueExW"]
        fn reg_query_value_ex_w(
            hkey: HKey,
            value_name: *const u16,
            reserved: *mut u32,
            value_type: *mut u32,
            data: *mut u8,
            data_len: *mut u32,
        ) -> i32;
        #[link_name = "RegCloseKey"]
        fn reg_close_key(hkey: HKey) -> i32;
    }

    pub fn is_light() -> bool {
        let path: Vec<u16> = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let value_name: Vec<u16> = "AppsUseLightTheme"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();

        let mut key: HKey = std::ptr::null_mut();
        let opened = unsafe {
            reg_open_key_ex_w(HKEY_CURRENT_USER, path.as_ptr(), 0, KEY_READ, &mut key)
        };
        if opened != 0 || key.is_null() {
            return false;
        }

        let mut value_type = 0u32;
        let mut value = 0u32;
        let mut value_len = std::mem::size_of::<u32>() as u32;
        let queried = unsafe {
            reg_query_value_ex_w(
                key,
                value_name.as_ptr(),
                std::ptr::null_mut(),
                &mut value_type,
                (&mut value as *mut u32).cast::<u8>(),
                &mut value_len,
            )
        };
        unsafe {
            reg_close_key(key);
        }

        queried == 0 && value_type == REG_DWORD && value_len == 4 && value == 1
    }
}

#[cfg(windows)]
fn detect_system_theme() -> bool {
    system_theme::is_light()
}

#[cfg(not(windows))]
fn detect_system_theme() -> bool {
    false
}

fn resolve_theme(preference: ThemePreference) -> bool {
    match preference {
        ThemePreference::System => detect_system_theme(),
        ThemePreference::Light => true,
        ThemePreference::Dark => false,
    }
}

#[cfg(windows)]
fn setup_fonts(ctx: &egui::Context) {
    let windows_dir = std::env::var_os("WINDIR").unwrap_or_else(|| "C:\\Windows".into());
    let path = PathBuf::from(windows_dir).join("Fonts").join("segoeui.ttf");
    let Ok(bytes) = std::fs::read(path) else {
        return;
    };

    let mut fonts = egui::FontDefinitions::default();
    fonts
        .font_data
        .insert("segoe_ui".to_owned(), egui::FontData::from_owned(bytes));
    if let Some(family) = fonts.families.get_mut(&egui::FontFamily::Proportional) {
        family.insert(0, "segoe_ui".to_owned());
    }
    ctx.set_fonts(fonts);
}

#[cfg(not(windows))]
fn setup_fonts(_ctx: &egui::Context) {}

struct Theme;

impl Theme {
    fn accent(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(0, 112, 200)
        } else {
            Color32::from_rgb(86, 200, 255)
        }
    }

    fn success(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(18, 128, 74)
        } else {
            Color32::from_rgb(92, 210, 145)
        }
    }

    fn warning(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(170, 96, 0)
        } else {
            Color32::from_rgb(255, 190, 64)
        }
    }

    fn error(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(196, 43, 28)
        } else {
            Color32::from_rgb(255, 135, 145)
        }
    }

    fn verify(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(111, 66, 193)
        } else {
            Color32::from_rgb(190, 155, 255)
        }
    }

    fn muted(light: bool) -> Color32 {
        if light {
            Color32::from_gray(96)
        } else {
            Color32::from_gray(168)
        }
    }

    fn text(light: bool) -> Color32 {
        if light {
            Color32::from_gray(24)
        } else {
            Color32::from_gray(244)
        }
    }

    fn panel(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(248, 250, 252)
        } else {
            Color32::from_rgb(28, 29, 32)
        }
    }

    fn card(light: bool) -> Color32 {
        if light {
            Color32::WHITE
        } else {
            Color32::from_rgb(36, 38, 42)
        }
    }

    fn selected(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(231, 245, 255)
        } else {
            Color32::from_rgb(24, 52, 70)
        }
    }

    fn border(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(216, 221, 228)
        } else {
            Color32::from_rgb(62, 66, 73)
        }
    }
}

fn apply_theme(ctx: &egui::Context, light: bool) {
    let mut visuals = if light {
        egui::Visuals::light()
    } else {
        egui::Visuals::dark()
    };
    visuals.panel_fill = Theme::panel(light);
    visuals.window_fill = Theme::panel(light);
    visuals.extreme_bg_color = Theme::card(light);
    visuals.widgets.noninteractive.bg_fill = Theme::panel(light);
    visuals.widgets.inactive.bg_fill = Theme::card(light);
    visuals.widgets.hovered.bg_stroke = egui::Stroke::new(1.0_f32, Theme::accent(light));
    visuals.selection.bg_fill = Theme::accent(light);
    visuals.override_text_color = Some(Theme::text(light));
    ctx.set_visuals(visuals);

    ctx.style_mut(|style| {
        style.spacing.item_spacing = egui::vec2(SPACING_SM, 7.0);
        style.spacing.button_padding = egui::vec2(12.0, 7.0);
        style.visuals.window_rounding = egui::Rounding::same(10.0);
    });
}

fn card_frame(light: bool) -> egui::Frame {
    egui::Frame::none()
        .fill(Theme::card(light))
        .stroke(egui::Stroke::new(1.0_f32, Theme::border(light)))
        .rounding(egui::Rounding::same(8.0))
        .inner_margin(egui::Margin::symmetric(14.0, 12.0))
}

fn format_bytes(bytes: u64) -> String {
    const KIB: f64 = 1024.0;
    const MIB: f64 = KIB * 1024.0;
    const GIB: f64 = MIB * 1024.0;
    let value = bytes as f64;
    if value >= GIB {
        format!("{:.2} GiB", value / GIB)
    } else if value >= MIB {
        format!("{:.1} MiB", value / MIB)
    } else if value >= KIB {
        format!("{:.1} KiB", value / KIB)
    } else {
        format!("{bytes} B")
    }
}

fn count_label(n: u64, singular: &str, plural: &str) -> String {
    let word = if n == 1 { singular } else { plural };
    format!("{n} {word}")
}

fn format_duration(secs: f64) -> String {
    if !secs.is_finite() || secs <= 0.0 {
        return "—".to_owned();
    }
    let total = secs.round() as u64;
    let hours = total / 3600;
    let minutes = (total % 3600) / 60;
    let seconds = total % 60;
    if hours > 0 {
        format!("{hours:02}:{minutes:02}:{seconds:02}")
    } else {
        format!("{minutes:02}:{seconds:02}")
    }
}

fn shown_bps(bps: f64, last_tick: Instant) -> f64 {
    let idle = last_tick.elapsed().as_secs_f64();
    if idle <= SPEED_DECAY_GRACE_SECS {
        bps
    } else {
        bps * (-(idle - SPEED_DECAY_GRACE_SECS) / SPEED_DECAY_TAU_SECS).exp()
    }
}

fn compact_path(path: &str, max_chars: usize) -> String {
    let chars: Vec<char> = path.chars().collect();
    if chars.len() <= max_chars || max_chars < 12 {
        return path.to_owned();
    }
    let head = (max_chars * 2) / 5;
    let tail = max_chars.saturating_sub(head + 1);
    format!(
        "{}…{}",
        chars[..head].iter().collect::<String>(),
        chars[chars.len() - tail..].iter().collect::<String>()
    )
}

fn phase_label(
    phase: DestPhase,
    errors: u64,
    light: bool,
    paused: bool,
) -> (&'static str, Color32) {
    if paused && matches!(phase, DestPhase::Copying | DestPhase::Verifying) {
        return ("PAUSADO", Theme::warning(light));
    }
    match phase {
        DestPhase::Idle => ("EN ESPERA", Theme::muted(light)),
        DestPhase::Copying => ("COPIANDO", Theme::accent(light)),
        DestPhase::Verifying => ("VERIFICANDO INTEGRIDAD", Theme::verify(light)),
        DestPhase::Done if errors > 0 => ("CON ERRORES", Theme::warning(light)),
        DestPhase::Done => ("COMPLETO", Theme::success(light)),
        DestPhase::Failed => ("ERROR", Theme::error(light)),
        DestPhase::Cancelled => ("CANCELADO", Theme::muted(light)),
    }
}

fn theme_glyph(theme: ThemePreference) -> &'static str {
    match theme {
        ThemePreference::System => "◐",
        ThemePreference::Light => "☀",
        ThemePreference::Dark => "●",
    }
}

fn theme_description(theme: ThemePreference) -> &'static str {
    match theme {
        ThemePreference::System => "Sigue automáticamente la apariencia de Windows.",
        ThemePreference::Light => "Interfaz clara para ambientes luminosos.",
        ThemePreference::Dark => "Interfaz oscura con menor brillo visual.",
    }
}
