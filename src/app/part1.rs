use crate::config::{load_settings, save_settings, AppSettings, ThemePreference};
use crate::copy_plan::{append_unique_destinations, same_path, CopyPlan};
use crate::engine::{format_bps, start_job, CopyOpts, DestPhase, JobState};
use crate::session;
use eframe::egui::{self, Color32, RichText};
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

type StartResult = Result<(Arc<JobState>, Vec<JoinHandle<()>>), String>;

#[derive(Clone, Debug)]
enum PendingDrop {
    Folder(PathBuf),
    File(PathBuf),
}

const SPACING_XS: f32 = 4.0;
const SPACING_SM: f32 = 8.0;
const SPACING_MD: f32 = 12.0;
const SPACING_LG: f32 = 20.0;
const FLUENT_RADIUS_SM: f32 = 6.0;
const FLUENT_RADIUS_MD: f32 = 8.0;
const FLUENT_RADIUS_LG: f32 = 12.0;
const FLUENT_CONTROL_HEIGHT: f32 = 32.0;
const RUNNING_REPAINT: Duration = Duration::from_millis(200);
const PAUSED_REPAINT: Duration = Duration::from_millis(500);
const STARTING_REPAINT: Duration = Duration::from_millis(80);
const ERROR_FLASH: Duration = Duration::from_secs(5);
const THEME_CHECK_INTERVAL: Duration = Duration::from_secs(10);
const PATH_CHECK_INTERVAL: Duration = Duration::from_secs(2);
const SPEED_DECAY_GRACE_SECS: f64 = 0.5;
const SPEED_DECAY_TAU_SECS: f64 = 2.0;
const DEFAULT_ACCENT_RGB: u32 = 0x0000_78D4;

static SYSTEM_ACCENT_RGB: AtomicU32 = AtomicU32::new(DEFAULT_ACCENT_RGB);

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

    #[link(name = "dwmapi")]
    extern "system" {
        #[link_name = "DwmGetColorizationColor"]
        fn dwm_get_colorization_color(colorization: *mut u32, opaque_blend: *mut i32) -> i32;
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

    pub fn accent_rgb() -> Option<u32> {
        let mut color = 0u32;
        let mut opaque = 0i32;
        let result = unsafe { dwm_get_colorization_color(&mut color, &mut opaque) };
        if result != 0 {
            return None;
        }
        Some(color & 0x00FF_FFFF)
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

fn refresh_system_accent() {
    #[cfg(windows)]
    if let Some(rgb) = system_theme::accent_rgb() {
        SYSTEM_ACCENT_RGB.store(rgb, Ordering::Relaxed);
    }
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
    let fonts_dir = PathBuf::from(windows_dir).join("Fonts");

    let mut fonts = egui::FontDefinitions::default();
    for (name, file) in [
        ("segoe_ui", "segoeui.ttf"),
        ("segoe_symbols", "seguisym.ttf"),
    ] {
        if let Ok(bytes) = std::fs::read(fonts_dir.join(file)) {
            fonts
                .font_data
                .insert(name.to_owned(), egui::FontData::from_owned(bytes));
        }
    }

    if let Some(family) = fonts.families.get_mut(&egui::FontFamily::Proportional) {
        if fonts.font_data.contains_key("segoe_symbols") {
            family.push("segoe_symbols".to_owned());
        }
        if fonts.font_data.contains_key("segoe_ui") {
            family.insert(0, "segoe_ui".to_owned());
        }
    }
    ctx.set_fonts(fonts);
}

#[cfg(not(windows))]
fn setup_fonts(_ctx: &egui::Context) {}

struct Theme;

impl Theme {
    fn accent(_light: bool) -> Color32 {
        let rgb = SYSTEM_ACCENT_RGB.load(Ordering::Relaxed);
        Color32::from_rgb(
            ((rgb >> 16) & 0xFF) as u8,
            ((rgb >> 8) & 0xFF) as u8,
            (rgb & 0xFF) as u8,
        )
    }

    fn on_accent(light: bool) -> Color32 {
        let accent = Self::accent(light);
        let luminance = (u32::from(accent.r()) * 299
            + u32::from(accent.g()) * 587
            + u32::from(accent.b()) * 114)
            / 1000;
        if luminance >= 150 {
            Color32::from_rgb(18, 18, 18)
        } else {
            Color32::WHITE
        }
    }

    fn blend(base: Color32, tint: Color32, tint_percent: u16) -> Color32 {
        let tint_percent = tint_percent.min(100);
        let base_percent = 100 - tint_percent;
        let mix = |a: u8, b: u8| -> u8 {
            (((u16::from(a) * base_percent) + (u16::from(b) * tint_percent)) / 100) as u8
        };
        Color32::from_rgb(
            mix(base.r(), tint.r()),
            mix(base.g(), tint.g()),
            mix(base.b(), tint.b()),
        )
    }

    fn success(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(16, 124, 65)
        } else {
            Color32::from_rgb(95, 210, 145)
        }
    }

    fn warning(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(157, 93, 0)
        } else {
            Color32::from_rgb(255, 185, 0)
        }
    }

    fn error(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(196, 43, 28)
        } else {
            Color32::from_rgb(255, 153, 164)
        }
    }

    fn verify(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(103, 74, 181)
        } else {
            Color32::from_rgb(194, 165, 255)
        }
    }

    fn muted(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(96, 96, 96)
        } else {
            Color32::from_rgb(173, 173, 173)
        }
    }

    fn text(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(27, 27, 27)
        } else {
            Color32::from_rgb(255, 255, 255)
        }
    }

    fn panel(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(246, 246, 246)
        } else {
            Color32::from_rgb(28, 28, 28)
        }
    }

    fn window(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(243, 243, 243)
        } else {
            Color32::from_rgb(32, 32, 32)
        }
    }

    fn card(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(253, 253, 253)
        } else {
            Color32::from_rgb(44, 44, 44)
        }
    }

    fn selected(light: bool) -> Color32 {
        let percent = if light { 8 } else { 16 };
        Self::blend(Self::card(light), Self::accent(light), percent)
    }

    fn border(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(224, 224, 224)
        } else {
            Color32::from_rgb(58, 58, 58)
        }
    }
}

fn apply_theme(ctx: &egui::Context, light: bool) {
    let mut visuals = if light {
        egui::Visuals::light()
    } else {
        egui::Visuals::dark()
    };
    let accent = Theme::accent(light);
    visuals.panel_fill = Theme::panel(light);
    visuals.window_fill = Theme::window(light);
    visuals.extreme_bg_color = Theme::card(light);
    visuals.widgets.noninteractive.bg_fill = Theme::panel(light);
    visuals.widgets.inactive.bg_fill = Theme::card(light);
    visuals.widgets.hovered.bg_fill = Theme::selected(light);
    visuals.widgets.hovered.bg_stroke = egui::Stroke::new(1.0_f32, accent);
    visuals.widgets.active.bg_fill = Theme::selected(light);
    visuals.widgets.active.bg_stroke = egui::Stroke::new(1.0_f32, accent);
    visuals.selection.bg_fill = accent;
    visuals.selection.stroke.color = accent;
    visuals.override_text_color = Some(Theme::text(light));
    ctx.set_visuals(visuals);

    ctx.style_mut(|style| {
        style.spacing.item_spacing = egui::vec2(SPACING_SM, SPACING_SM);
        style.spacing.button_padding = egui::vec2(14.0, 7.0);
        style.spacing.interact_size.y = FLUENT_CONTROL_HEIGHT;
        style.visuals.window_rounding = egui::Rounding::same(FLUENT_RADIUS_LG);
    });
}

fn card_frame(light: bool) -> egui::Frame {
    egui::Frame::none()
        .fill(Theme::card(light))
        .stroke(egui::Stroke::new(1.0_f32, Theme::border(light)))
        .rounding(egui::Rounding::same(FLUENT_RADIUS_MD))
        .inner_margin(egui::Margin::symmetric(14.0, 11.0))
}

fn window_frame(ctx: &egui::Context, light: bool) -> egui::Frame {
    egui::Frame::window(&ctx.style())
        .fill(Theme::window(light))
        .stroke(egui::Stroke::new(1.0_f32, Theme::border(light)))
        .rounding(egui::Rounding::same(FLUENT_RADIUS_LG))
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

fn progress_fraction(written: u64, total: u64, phase: DestPhase) -> f32 {
    if total == 0 {
        return if phase == DestPhase::Done { 1.0_f32 } else { 0.0_f32 };
    }
    (written as f64 / total as f64).clamp(0.0, 1.0) as f32
}

fn source_layout_stacked(available_width: f32) -> bool {
    available_width < 720.0
}

fn start_disabled_reason(
    source: &str,
    destination_count: usize,
    path_error_count: usize,
) -> Option<&'static str> {
    if source.trim().is_empty() {
        Some("Selecciona un archivo o carpeta de origen")
    } else if destination_count == 0 {
        Some("Agrega al menos un destino")
    } else if path_error_count > 0 {
        Some("Corrige los problemas de ruta antes de iniciar")
    } else {
        None
    }
}

#[cfg(test)]
fn contains_mojibake(text: &str) -> bool {
    [
        "\u{FFFD}",
        "\u{00C3}",
        "\u{00C2}",
        "\u{00E2}\u{20AC}",
        "\u{00F0}\u{0178}",
    ]
    .iter()
    .any(|marker| text.contains(marker))
}

fn shown_bps(bps: f64, last_tick: Instant) -> f64 {
    let idle = last_tick.elapsed().as_secs_f64();
    if idle <= SPEED_DECAY_GRACE_SECS {
        bps
    } else {
        bps * (-(idle - SPEED_DECAY_GRACE_SECS) / SPEED_DECAY_TAU_SECS).exp()
    }
}

fn ui_copy_active(engine_running: bool, all_terminal: bool) -> bool {
    engine_running && !all_terminal
}

fn drop_input_locked(starting: bool, visual_running: bool) -> bool {
    starting || visual_running
}

fn visible_bps(bps: f64, last_tick: Instant, phase: DestPhase, paused: bool) -> f64 {
    if paused || phase != DestPhase::Copying {
        0.0
    } else {
        shown_bps(bps, last_tick)
    }
}

fn logical_fanout_bps(speeds: impl IntoIterator<Item = f64>) -> f64 {
    let mut speeds = speeds.into_iter();
    let Some(first) = speeds.next() else {
        return 0.0;
    };
    speeds.fold(first, f64::min).max(0.0)
}

fn can_start_new_job(starting: bool, engine_running: bool) -> bool {
    !starting && !engine_running
}

fn drop_action_button(
    ui: &mut egui::Ui,
    label: &str,
    primary: bool,
    light: bool,
) -> egui::Response {
    let accent = Theme::accent(light);
    let button = if primary {
        egui::Button::new(RichText::new(label).strong().color(Theme::on_accent(light)))
            .fill(accent)
            .stroke(egui::Stroke::NONE)
            .rounding(egui::Rounding::same(10.0))
    } else {
        egui::Button::new(RichText::new(label).strong().color(accent))
            .fill(Theme::selected(light))
            .stroke(egui::Stroke::new(1.0_f32, Theme::border(light)))
            .rounding(egui::Rounding::same(10.0))
    };
    ui.add_sized([ui.available_width(), 40.0], button)
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
        ThemePreference::System => "⊞",
        ThemePreference::Light => "☀",
        ThemePreference::Dark => "☾",
    }
}

fn theme_description(theme: ThemePreference) -> &'static str {
    match theme {
        ThemePreference::System => "Usa automáticamente el tema y el color de énfasis de Windows.",
        ThemePreference::Light => "Mantiene la interfaz clara usando el color de énfasis de Windows.",
        ThemePreference::Dark => "Mantiene la interfaz oscura usando el color de énfasis de Windows.",
    }
}

fn theme_card_caption(theme: ThemePreference) -> &'static str {
    match theme {
        ThemePreference::System => "Automático",
        ThemePreference::Light => "Siempre claro",
        ThemePreference::Dark => "Siempre oscuro",
    }
}
