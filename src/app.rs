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
            reg_open_key_ex_w(
                HKEY_CURRENT_USER,
                path.as_ptr(),
                0,
                KEY_READ,
                &mut key,
            )
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

#[cfg(windows)]
fn setup_fonts(ctx: &egui::Context) {
    let windows_dir = std::env::var_os("WINDIR").unwrap_or_else(|| "C:\\Windows".into());
    let segoe_path = PathBuf::from(windows_dir).join("Fonts").join("segoeui.ttf");

    let Ok(bytes) = std::fs::read(segoe_path) else {
        return;
    };

    let mut fonts = egui::FontDefinitions::default();
    fonts.font_data.insert(
        "segoe_ui".to_owned(),
        egui::FontData::from_owned(bytes),
    );
    if let Some(family) = fonts
        .families
        .get_mut(&egui::FontFamily::Proportional)
    {
        family.insert(0, "segoe_ui".to_owned());
    }
    ctx.set_fonts(fonts);
}

#[cfg(not(windows))]
fn setup_fonts(_ctx: &egui::Context) {}

struct Theme;

impl Theme {
    fn success(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(16, 124, 16)
        } else {
            Color32::from_rgb(108, 203, 95)
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

    fn info(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(0, 120, 212)
        } else {
            Color32::from_rgb(96, 205, 255)
        }
    }

    fn verify(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(135, 100, 195)
        } else {
            Color32::from_rgb(175, 150, 220)
        }
    }

    fn muted(light: bool) -> Color32 {
        if light {
            Color32::from_gray(117)
        } else {
            Color32::from_gray(166)
        }
    }

    fn accent(light: bool) -> Color32 {
        Self::info(light)
    }

    fn progress_bar(light: bool) -> Color32 {
        Self::info(light)
    }

    fn bg_secondary(light: bool) -> Color32 {
        if light {
            Color32::from_rgb(243, 243, 243)
        } else {
            Color32::from_rgb(32, 32, 32)
        }
    }

    fn text_primary(light: bool) -> Color32 {
        if light {
            Color32::from_gray(0)
        } else {
            Color32::from_gray(255)
        }
    }
}

fn apply_theme(ctx: &egui::Context, light: bool) {
    let mut visuals = if light {
        egui::Visuals::light()
    } else {
        egui::Visuals::dark()
    };
    visuals.widgets.noninteractive.bg_fill = Theme::bg_secondary(light);
    visuals.widgets.inactive.bg_fill = Theme::bg_secondary(light);
    visuals.override_text_color = Some(Theme::text_primary(light));
    visuals.selection.bg_fill = Theme::accent(light);
    visuals.selection.stroke.color = Theme::accent(light);
    ctx.set_visuals(visuals);
    ctx.style_mut(|style| {
        style.spacing.item_spacing = egui::vec2(SPACING_SM, 7.0);
        style.spacing.button_padding = egui::vec2(12.0, 6.0);
        style.text_styles = [
            (
                egui::TextStyle::Heading,
                egui::FontId::new(20.0, egui::FontFamily::Proportional),
            ),
            (
                egui::TextStyle::Body,
                egui::FontId::new(14.0, egui::FontFamily::Proportional),
            ),
            (
                egui::TextStyle::Monospace,
                egui::FontId::new(12.5, egui::FontFamily::Monospace),
            ),
            (
                egui::TextStyle::Button,
                egui::FontId::new(14.0, egui::FontFamily::Proportional),
            ),
            (
                egui::TextStyle::Small,
                egui::FontId::new(12.0, egui::FontFamily::Proportional),
            ),
        ]
        .into();
    });
}

fn format_bytes(bytes: u64) -> String {
    const KIB: f64 = 1024.0;
    const MIB: f64 = KIB * 1024.0;
    const GIB: f64 = MIB * 1024.0;
    let b = bytes as f64;
    if b >= GIB {
        format!("{:.2} GiB", b / GIB)
    } else if b >= MIB {
        format!("{:.1} MiB", b / MIB)
    } else if b >= KIB {
        format!("{:.1} KiB", b / KIB)
    } else {
        format!("{} B", bytes)
    }
}

fn count_label(n: u64, singular: &str, plural: &str) -> String {
    if n == 1 {
        format!("{n} {singular}")
    } else {
        format!("{n} {plural}")
    }
}

fn format_duration(secs: f64) -> String {
    if !secs.is_finite() || secs <= 0.0 {
        return "—".into();
    }
    let total = secs.round() as u64;
    let h = total / 3600;
    let m = (total % 3600) / 60;
    let s = total % 60;
    if h > 0 {
        format!("{:02}:{:02}:{:02}", h, m, s)
    } else {
        format!("{:02}:{:02}", m, s)
    }
}

fn shown_bps(bps_recent: f64, last_tick: Instant) -> f64 {
    let idle = last_tick.elapsed().as_secs_f64();
    if idle <= SPEED_DECAY_GRACE_SECS {
        bps_recent
    } else {
        bps_recent * (-(idle - SPEED_DECAY_GRACE_SECS) / SPEED_DECAY_TAU_SECS).exp()
    }
}

fn phase_label(p: DestPhase, files_err: u64, light: bool) -> (&'static str, Color32) {
    match p {
        DestPhase::Idle => ("EN ESPERA", Theme::muted(light)),
        DestPhase::Copying => ("COPIANDO", Theme::info(light)),
        DestPhase::Verifying => ("VERIFICANDO", Theme::verify(light)),
        DestPhase::Done if files_err > 0 => ("CON ERRORES", Theme::warning(light)),
        DestPhase::Done => ("COMPLETO", Theme::success(light)),
        DestPhase::Failed => ("ERROR", Theme::error(light)),
        DestPhase::Cancelled => ("CANCELADO", Theme::muted(light)),
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

pub struct CopierApp {
    source: String,
    dests: Vec<String>,
    verify: bool,
    skip_same: bool,
    keep_going: bool,
    status: String,
    job: Option<Arc<JobState>>,
    workers: Vec<JoinHandle<()>>,
    startup_rx: Option<mpsc::Receiver<StartResult>>,
    show_credits: bool,
    error_flash_until: Option<Instant>,
    use_light_theme: bool,
    last_theme_check: Instant,
    applied_theme: Option<bool>,
    fonts_initialized: bool,
    path_errors: Vec<String>,
    last_path_check: Instant,
    paths_key: u64,
}

impl CopierApp {
    pub fn new() -> Self {
        Self {
            source: String::new(),
            dests: Vec::new(),
            verify: false,
            skip_same: true,
            keep_going: true,
            status: "Listo para copiar una carpeta a múltiples destinos".into(),
            job: None,
            workers: Vec::new(),
            startup_rx: None,
            show_credits: false,
            error_flash_until: None,
            use_light_theme: detect_system_theme(),
            last_theme_check: Instant::now(),
            applied_theme: None,
            fonts_initialized: false,
            path_errors: Vec::new(),
            last_path_check: Instant::now(),
            paths_key: u64::MAX,
        }
    }

    fn starting(&self) -> bool {
        self.startup_rx.is_some()
    }

    fn running_job(&self) -> bool {
        self.job
            .as_ref()
            .is_some_and(|j| j.running.load(Ordering::Relaxed))
    }

    fn pick_dir() -> Option<String> {
        rfd::FileDialog::new()
            .pick_folder()
            .map(|p| p.to_string_lossy().into_owned())
    }

    fn validate_paths(&self) -> Vec<String> {
        let mut errors = Vec::new();
        let source = self.source.trim();
        if !source.is_empty() {
            let path = PathBuf::from(source);
            if !path.exists() {
                errors.push("El origen no existe.".into());
            } else if !path.is_dir() {
                errors.push("El origen no es una carpeta.".into());
            }
        }
        for (i, dest) in self.dests.iter().enumerate() {
            let path = PathBuf::from(dest.trim());
            if !path.exists() {
                errors.push(format!(
                    "El destino {} no existe o no está disponible.",
                    i + 1
                ));
            } else if !path.is_dir() {
                errors.push(format!("El destino {} no es una carpeta.", i + 1));
            }
        }
        errors
    }

    fn paths_key(&self) -> u64 {
        let mut hasher = DefaultHasher::new();
        self.source.hash(&mut hasher);
        self.dests.hash(&mut hasher);
        hasher.finish()
    }

    fn flash_error(&mut self, message: String) {
        self.status = message;
        self.error_flash_until = Some(Instant::now() + ERROR_FLASH);
    }

    fn start(&mut self) {
        if self.source.trim().is_empty() {
            self.flash_error("Selecciona una carpeta de origen.".into());
            return;
        }
        if self.dests.is_empty() {
            self.flash_error("Agrega al menos un destino.".into());
            return;
        }
        if let Some(first) = self.validate_paths().into_iter().next() {
            self.flash_error(first);
            return;
        }
        let src = PathBuf::from(self.source.trim());
        let dests: Vec<PathBuf> = self
            .dests
            .iter()
            .map(|s| PathBuf::from(s.trim()))
            .filter(|p| !p.as_os_str().is_empty())
            .collect();
        let opts = CopyOpts {
            verify: self.verify,
            skip_same: self.skip_same,
            keep_going: self.keep_going,
        };
        let (tx, rx) = mpsc::channel();
        self.job = None;
        self.workers.clear();
        self.error_flash_until = None;
        self.status = "Analizando origen y destinos…".into();
        self.startup_rx = Some(rx);
        thread::spawn(move || {
            let _ = tx.send(start_job(src, dests, opts));
        });
    }

    fn poll_startup(&mut self) {
        let outcome = self.startup_rx.as_ref().and_then(|rx| match rx.try_recv() {
            Ok(result) => Some(Ok(result)),
            Err(mpsc::TryRecvError::Disconnected) => {
                Some(Err("El análisis previo terminó inesperadamente.".to_owned()))
            }
            Err(mpsc::TryRecvError::Empty) => None,
        });
        let Some(outcome) = outcome else {
            return;
        };
        self.startup_rx = None;
        match outcome {
            Ok(Ok((state, handles))) => {
                self.error_flash_until = None;
                let n_files = state.files_total.load(Ordering::Relaxed);
                let n_dests = state.dests.lock().map(|d| d.len()).unwrap_or(0) as u64;
                self.status = format!(
                    "Comprobación correcta · {} · {} · {}",
                    count_label(n_files, "archivo", "archivos"),
                    format_bytes(state.bytes_total.load(Ordering::Relaxed)),
                    count_label(n_dests, "destino", "destinos")
                );
                self.job = Some(state);
                self.workers = handles;
            }
            Ok(Err(e)) | Err(e) => self.flash_error(e),
        }
    }
}

impl eframe::App for CopierApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if !self.fonts_initialized {
            setup_fonts(ctx);
            self.fonts_initialized = true;
        }
        self.poll_startup();
        if self.last_theme_check.elapsed() >= THEME_CHECK_INTERVAL {
            self.use_light_theme = detect_system_theme();
            self.last_theme_check = Instant::now();
        }
        if self.applied_theme != Some(self.use_light_theme) {
            apply_theme(ctx, self.use_light_theme);
            self.applied_theme = Some(self.use_light_theme);
        }

        let snaps = self
            .job
            .as_ref()
            .map(|job| job.snapshot())
            .unwrap_or_default();
        let all_terminal = !snaps.is_empty()
            && snaps.iter().all(|d| {
                matches!(
                    d.phase,
                    DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled
                )
            });
        let starting = self.starting();
        let running = self.running_job() && !all_terminal;
        let busy = starting || running;
        let now = Instant::now();

        if let Some(until) = self.error_flash_until {
            if now >= until {
                self.error_flash_until = None;
            } else {
                ctx.request_repaint_after(until.saturating_duration_since(now));
            }
        }
        if starting {
            ctx.request_repaint_after(STARTING_REPAINT);
        } else if running {
            let paused = self
                .job
                .as_ref()
                .is_some_and(|j| j.pause.load(Ordering::Relaxed));
            ctx.request_repaint_after(if paused {
                PAUSED_REPAINT
            } else {
                RUNNING_REPAINT
            });
        } else {
            ctx.request_repaint_after(THEME_CHECK_INTERVAL);
        }

        let key = self.paths_key();
        if key != self.paths_key || self.last_path_check.elapsed() >= PATH_CHECK_INTERVAL {
            self.paths_key = key;
            self.last_path_check = Instant::now();
            self.path_errors = if busy {
                Vec::new()
            } else {
                self.validate_paths()
            };
        }
        let path_error_count = self.path_errors.len();
        let path_error_hover = if path_error_count == 0 {
            String::new()
        } else {
            self.path_errors.join("\n")
        };
        let ready_to_start = !self.source.trim().is_empty()
            && !self.dests.is_empty()
            && path_error_count == 0;
        let error_flash_active = self.error_flash_until.is_some();

        egui::TopBottomPanel::top("header").show(ctx, |ui| {
            ui.add_space(SPACING_XS);
            ui.horizontal(|ui| {
                ui.heading(RichText::new("RepartoCopier").strong());
                ui.weak("Una carpeta · múltiples destinos");
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if starting {
                        ui.weak("Validando…");
                    }
                });
            });
            ui.add_space(SPACING_XS);
        });

        egui::TopBottomPanel::bottom("footer").show(ctx, |ui| {
            ui.horizontal(|ui| {
                if error_flash_active {
                    ui.colored_label(Theme::error(self.use_light_theme), &self.status);
                } else {
                    ui.weak(&self.status);
                }
                if starting {
                    ui.separator();
                    ui.weak("Analizando archivos…");
                } else if running {
                    ui.separator();
                    ui.weak("Copia en curso");
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.small_button("Acerca de").clicked() {
                        self.show_credits = true;
                    }
                });
            });
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.add_space(SPACING_XS);
            ui.horizontal(|ui| {
                ui.label(RichText::new("ORIGEN").strong());
                ui.add_enabled_ui(!busy, |ui| {
                    ui.add(
                        egui::TextEdit::singleline(&mut self.source)
                            .desired_width(ui.available_width() - 90.0)
                            .hint_text("Selecciona la carpeta que quieres copiar"),
                    );
                    if ui.button("Examinar").clicked() {
                        if let Some(p) = Self::pick_dir() {
                            self.source = p;
                        }
                    }
                });
            });

            ui.add_space(SPACING_XS);
            ui.horizontal(|ui| {
                let destinations_label = if self.dests.len() == 1 {
                    "DESTINO"
                } else {
                    "DESTINOS"
                };
                ui.label(
                    RichText::new(format!("{destinations_label}  ({})", self.dests.len())).strong(),
                );
                ui.add_enabled_ui(!busy, |ui| {
                    if ui.button("+ Agregar").clicked() {
                        if let Some(p) = Self::pick_dir() {
                            if !self.dests.contains(&p) {
                                self.dests.push(p);
                            }
                        }
                    }
                });
            });

            egui::ScrollArea::vertical()
                .id_salt("destinations")
                .max_height(150.0)
                .show(ui, |ui| {
                    let mut remove = None;
                    for (i, d) in self.dests.iter().enumerate() {
                        ui.horizontal(|ui| {
                            ui.weak(format!("{:02}", i + 1));
                            ui.label(compact_path(d, 66)).on_hover_text(d);
                            if let Some(dp) = snaps.get(i) {
                                let (label, color) =
                                    phase_label(dp.phase, dp.files_err, self.use_light_theme);
                                ui.colored_label(color, format!("[{label}]"));
                            }
                            if !busy && ui.small_button("×").clicked() {
                                remove = Some(i);
                            }
                        });
                    }
                    if let Some(i) = remove {
                        self.dests.remove(i);
                    }
                });

            if path_error_count > 0 {
                ui.colored_label(
                    Theme::warning(self.use_light_theme),
                    format!("⚠ {} problema(s) de ruta detectado(s)", path_error_count),
                )
                .on_hover_text(&path_error_hover);
            }

            ui.separator();
            ui.horizontal(|ui| {
                ui.add_enabled_ui(!busy, |ui| {
                    ui.checkbox(&mut self.skip_same, "Omitir iguales")
                        .on_hover_text("Evita volver a copiar archivos que ya coinciden.");
                    ui.checkbox(&mut self.keep_going, "Continuar con errores")
                        .on_hover_text("Mantiene activos los demás destinos si uno falla.");
                    ui.checkbox(&mut self.verify, "Verificar integridad")
                        .on_hover_text("Comprueba el contenido copiado mediante BLAKE3.");
                });
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if running {
                        if let Some(job) = &self.job {
                            let paused = job.pause.load(Ordering::Relaxed);
                            if ui
                                .button(if paused { "Continuar" } else { "Pausar" })
                                .clicked()
                            {
                                job.pause.store(!paused, Ordering::Relaxed);
                            }
                            if ui.button("Cancelar").clicked() {
                                job.cancel.store(true, Ordering::Relaxed);
                                job.pause.store(false, Ordering::Relaxed);
                            }
                        }
                    } else if !starting {
                        let start_button =
                            egui::Button::new(RichText::new("Iniciar copia").strong());
                        if ui.add_enabled(ready_to_start, start_button).clicked() {
                            self.start();
                        }
                    }
                    if starting {
                        ui.weak("Analizando…");
                    }
                });
            });

            if starting {
                ui.add_space(SPACING_MD);
                ui.centered_and_justified(|ui| {
                    ui.label(
                        RichText::new("Preparando la copia…")
                            .size(20.0)
                            .strong(),
                    );
                });
            } else if let Some(job) = &self.job {
                if !snaps.is_empty() {
                    let avg_progress = snaps
                        .iter()
                        .map(|d| {
                            if d.total == 0 {
                                if d.phase == DestPhase::Done {
                                    1.0
                                } else {
                                    0.0
                                }
                            } else {
                                d.written as f64 / d.total as f64
                            }
                        })
                        .sum::<f64>()
                        / snaps.len() as f64;
                    let total_bps: f64 = snaps
                        .iter()
                        .map(|d| shown_bps(d.bps_recent, d.last_tick))
                        .sum();
                    let mut eta_known = true;
                    let mut eta = 0.0f64;
                    for dp in &snaps {
                        let remaining = dp.total.saturating_sub(dp.written);
                        if remaining == 0
                            || matches!(
                                dp.phase,
                                DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled
                            )
                        {
                            continue;
                        }
                        if dp.bps <= 0.0 {
                            eta_known = false;
                            break;
                        }
                        eta = eta.max(remaining as f64 / dp.bps);
                    }
                    let eta_text = if eta_known {
                        format_duration(eta)
                    } else {
                        "—".into()
                    };

                    ui.add_space(SPACING_SM);
                    ui.label(RichText::new("Progreso general").strong());
                    ui.add(
                        egui::ProgressBar::new(avg_progress as f32)
                            .desired_height(22.0)
                            .fill(Theme::progress_bar(self.use_light_theme))
                            .show_percentage(),
                    );
                    ui.horizontal(|ui| {
                        ui.heading(format_bps(total_bps));
                        ui.weak(format!(
                            "{} · Tiempo restante {}",
                            format_bytes(job.bytes_total.load(Ordering::Relaxed)),
                            eta_text
                        ));
                    });
                    ui.add_space(SPACING_SM);

                    egui::ScrollArea::vertical()
                        .id_salt("dest-progress")
                        .max_height(300.0)
                        .show(ui, |ui| {
                            egui::Grid::new("dest-grid")
                                .striped(true)
                                .num_columns(4)
                                .spacing([20.0, 9.0])
                                .show(ui, |ui| {
                                    ui.strong("Destino");
                                    ui.strong("Velocidad");
                                    ui.strong("Progreso");
                                    ui.strong("Estado");
                                    ui.end_row();
                                    for dp in &snaps {
                                        let frac = if dp.total == 0 {
                                            if dp.phase == DestPhase::Done {
                                                1.0
                                            } else {
                                                0.0
                                            }
                                        } else {
                                            (dp.written as f32 / dp.total as f32).clamp(0.0, 1.0)
                                        };
                                        let (label, color) = phase_label(
                                            dp.phase,
                                            dp.files_err,
                                            self.use_light_theme,
                                        );
                                        let path_response = ui.label(
                                            egui::RichText::new(compact_path(&dp.label, 36)).small(),
                                        );
                                        if dp.last_file.is_empty() {
                                            path_response.on_hover_text(&dp.label);
                                        } else {
                                            path_response.on_hover_text(format!(
                                                "{}\nArchivo: {}",
                                                dp.label, dp.last_file
                                            ));
                                        }
                                        ui.label(format_bps(shown_bps(
                                            dp.bps_recent,
                                            dp.last_tick,
                                        )));
                                        ui.add(
                                            egui::ProgressBar::new(frac)
                                                .desired_width(175.0)
                                                .fill(Theme::progress_bar(self.use_light_theme))
                                                .show_percentage(),
                                        );
                                        let response = ui.colored_label(color, label);
                                        if let Some(error) = &dp.error {
                                            response.on_hover_text(error);
                                        }
                                        ui.end_row();
                                    }
                                });
                        });

                    ui.add_space(SPACING_SM);
                    ui.horizontal(|ui| {
                        ui.weak(count_label(
                            snaps.len() as u64,
                            "destino seleccionado",
                            "destinos seleccionados",
                        ));
                        if self.verify {
                            ui.separator();
                            ui.weak("Verificación de integridad activada");
                        }
                    });
                }
            } else {
                ui.add_space(SPACING_MD);
                ui.centered_and_justified(|ui| {
                    ui.label(RichText::new("Listo para copiar").size(22.0).strong());
                });
            }
        });

        if self.show_credits {
            let mut open = true;
            egui::Window::new("Acerca de RepartoCopier")
                .collapsible(false)
                .resizable(false)
                .default_width(440.0)
                .open(&mut open)
                .show(ctx, |ui| {
                    ui.vertical_centered(|ui| {
                        ui.add_space(SPACING_SM);
                        ui.heading(RichText::new("RepartoCopier").strong().size(24.0));
                        ui.label(
                            RichText::new("Una carpeta · múltiples destinos")
                                .color(Theme::muted(self.use_light_theme)),
                        );
                        ui.add_space(SPACING_XS);
                        ui.label(
                            RichText::new(format!("Versión {}", env!("CARGO_PKG_VERSION")))
                                .small(),
                        );

                        ui.add_space(SPACING_MD);
                        ui.separator();
                        ui.add_space(SPACING_MD);
                        ui.label(RichText::new("Simple por fuera. Potente por dentro.").strong());
                        ui.add_space(SPACING_XS);
                        ui.label(
                            "Creado para realizar copias múltiples de forma rápida, segura y confiable.",
                        );

                        ui.add_space(SPACING_MD);
                        ui.label(RichText::new("Desarrollado por").small());
                        ui.label(RichText::new("ReinierTutoriales").strong().size(17.0));
                        ui.label(RichText::new("© 2026").small());

                        ui.add_space(SPACING_MD);
                        ui.separator();
                        ui.add_space(SPACING_MD);
                        ui.label(RichText::new("¿Te resulta útil RepartoCopier?").strong());
                        ui.label("Apoya el proyecto dejando una ⭐ en GitHub. ❤️");
                        ui.add_space(SPACING_SM);
                        ui.hyperlink_to(
                            "Abrir repositorio en GitHub ↗",
                            "https://github.com/ReinierTutoriales/disk-duplicator",
                        );

                        ui.add_space(SPACING_MD);
                        ui.separator();
                        ui.add_space(SPACING_SM);
                        ui.label(RichText::new("Código abierto · Licencia MIT").small());
                        ui.add_space(SPACING_XS);
                    });
                });
            self.show_credits = open;
        }

        if !busy && all_terminal {
            let done_ok = snaps
                .iter()
                .filter(|d| d.phase == DestPhase::Done && d.files_err == 0)
                .count();
            let done_err = snaps
                .iter()
                .filter(|d| d.phase == DestPhase::Done && d.files_err > 0)
                .count();
            let failed = snaps
                .iter()
                .filter(|d| d.phase == DestPhase::Failed)
                .count();
            let cancelled = snaps
                .iter()
                .filter(|d| d.phase == DestPhase::Cancelled)
                .count();
            self.status = match (failed, cancelled) {
                (0, 0) if done_err == 0 => {
                    format!("Completado · {done_ok}/{} sin errores", snaps.len())
                }
                (0, 0) => format!(
                    "Finalizado con errores · {} · {}",
                    count_label(done_err as u64, "destino con errores", "destinos con errores"),
                    count_label(done_ok as u64, "destino correcto", "destinos correctos")
                ),
                (f, 0) => format!(
                    "Finalizado con errores · {} · {}",
                    count_label(f as u64, "destino fallido", "destinos fallidos"),
                    count_label(done_ok as u64, "destino correcto", "destinos correctos")
                ),
                (0, c) => format!(
                    "Cancelado · {}/{} destinos alcanzados · {}",
                    done_ok + done_err,
                    snaps.len(),
                    count_label(c as u64, "cancelado", "cancelados")
                ),
                (f, c) => format!(
                    "Cancelado con errores · {} · {}",
                    count_label(f as u64, "fallido", "fallidos"),
                    count_label(c as u64, "cancelado", "cancelados")
                ),
            };
        }
    }
}
