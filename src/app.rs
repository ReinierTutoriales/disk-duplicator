use crate::engine::{format_bps, start_job, CopyOpts, DestPhase, JobState};
use eframe::egui::{self, Color32, RichText};
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

struct Theme;

impl Theme {
    fn success(light: bool) -> Color32 {
        if light { Color32::from_rgb(0, 115, 80) } else { Color32::from_rgb(80, 210, 140) }
    }

    fn warning(light: bool) -> Color32 {
        if light { Color32::from_rgb(130, 85, 0) } else { Color32::from_rgb(235, 175, 70) }
    }

    fn error(light: bool) -> Color32 {
        if light { Color32::from_rgb(190, 35, 35) } else { Color32::from_rgb(255, 95, 95) }
    }

    fn info(light: bool) -> Color32 {
        if light { Color32::from_rgb(0, 90, 180) } else { Color32::from_rgb(90, 170, 255) }
    }

    fn verify(light: bool) -> Color32 {
        if light { Color32::from_rgb(105, 65, 180) } else { Color32::from_rgb(180, 130, 255) }
    }

    fn muted(light: bool) -> Color32 {
        if light { Color32::from_gray(90) } else { Color32::from_gray(150) }
    }

    fn accent(light: bool) -> Color32 {
        if light { Color32::from_rgb(0, 95, 190) } else { Color32::from_rgb(100, 180, 255) }
    }

    fn bg_secondary(light: bool) -> Color32 {
        if light { Color32::from_gray(245) } else { Color32::from_gray(30) }
    }

    fn text_primary(light: bool) -> Color32 {
        if light { Color32::from_gray(20) } else { Color32::from_gray(230) }
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
        style.spacing.item_spacing = egui::vec2(SPACING_SM, 6.0);
        style.spacing.button_padding = egui::vec2(10.0, 5.0);
        style.text_styles = [
            (
                egui::TextStyle::Heading,
                egui::FontId::new(18.0, egui::FontFamily::Proportional),
            ),
            (
                egui::TextStyle::Body,
                egui::FontId::new(13.0, egui::FontFamily::Proportional),
            ),
            (
                egui::TextStyle::Monospace,
                egui::FontId::new(12.0, egui::FontFamily::Monospace),
            ),
            (
                egui::TextStyle::Button,
                egui::FontId::new(13.0, egui::FontFamily::Proportional),
            ),
            (
                egui::TextStyle::Small,
                egui::FontId::new(11.0, egui::FontFamily::Proportional),
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
}

impl CopierApp {
    pub fn new() -> Self {
        Self {
            source: String::new(),
            dests: Vec::new(),
            verify: false,
            skip_same: true,
            keep_going: true,
            status: "1 origen → N destinos · almacenamiento HDD/SSD".into(),
            job: None,
            workers: Vec::new(),
            startup_rx: None,
            show_credits: false,
            error_flash_until: None,
            use_light_theme: detect_system_theme(),
            last_theme_check: Instant::now(),
            applied_theme: None,
        }
    }

    fn starting(&self) -> bool {
        self.startup_rx.is_some()
    }

    fn running_job(&self) -> bool {
        self.job
            .as_ref()
            .is_some_and(|j| j.running.load(Ordering::Relaxed))
            || self.workers.iter().any(|h| !h.is_finished())
    }

    fn busy(&self) -> bool {
        self.starting() || self.running_job()
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
                errors.push(format!("El destino {} no existe o no está disponible.", i + 1));
            } else if !path.is_dir() {
                errors.push(format!("El destino {} no es una carpeta.", i + 1));
            }
        }
        errors
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
            Err(mpsc::TryRecvError::Disconnected) => Some(Err(
                "El análisis previo terminó inesperadamente.".to_owned(),
            )),
            Err(mpsc::TryRecvError::Empty) => None,
        });

        let Some(outcome) = outcome else {
            return;
        };
        self.startup_rx = None;
        match outcome {
            Ok(Ok((state, handles))) => {
                self.error_flash_until = None;
                self.status = format!(
                    "Preflight correcto · {} archivos · {} · {} destinos",
                    state.files_total.load(Ordering::Relaxed),
                    format_bytes(state.bytes_total.load(Ordering::Relaxed)),
                    state.dests.lock().map(|d| d.len()).unwrap_or(0)
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
        self.poll_startup();

        if self.last_theme_check.elapsed() >= THEME_CHECK_INTERVAL {
            self.use_light_theme = detect_system_theme();
            self.last_theme_check = Instant::now();
        }
        if self.applied_theme != Some(self.use_light_theme) {
            apply_theme(ctx, self.use_light_theme);
            self.applied_theme = Some(self.use_light_theme);
        }

        let starting = self.starting();
        let running = self.running_job();
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
            ctx.request_repaint_after(if paused { PAUSED_REPAINT } else { RUNNING_REPAINT });
        } else {
            ctx.request_repaint_after(THEME_CHECK_INTERVAL);
        }

        // Exactly one progress snapshot per UI frame. The same clone is reused by
        // the destination list, progress grid and completion summary.
        let snaps = self.job.as_ref().map(|job| job.snapshot()).unwrap_or_default();
        let path_errors = if busy { Vec::new() } else { self.validate_paths() };
        let ready_to_start = !self.source.trim().is_empty()
            && !self.dests.is_empty()
            && path_errors.is_empty();
        let error_flash_active = self.error_flash_until.is_some();

        egui::TopBottomPanel::top("header").show(ctx, |ui| {
            ui.add_space(SPACING_XS);
            ui.horizontal(|ui| {
                ui.heading(RichText::new("DISK DUPLICATOR").strong());
                ui.weak("1 → N  ·  HDD / SSD");
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if starting {
                        ui.weak("preflight");
                    } else if let Some(job) = &self.job {
                        ui.weak(format!(
                            "buffers {}/{}",
                            job.buffers_in_flight.load(Ordering::Relaxed),
                            job.max_buffers
                        ));
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
                    ui.weak("analizando");
                } else if running {
                    ui.separator();
                    ui.weak("fan-out activo");
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.small_button("Créditos").clicked() {
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
                            .hint_text("Carpeta origen"),
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
                ui.label(RichText::new(format!("DESTINOS  ({})", self.dests.len())).strong());
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

            if !path_errors.is_empty() {
                ui.colored_label(
                    Theme::warning(self.use_light_theme),
                    format!("⚠ {} problema(s) de ruta detectado(s)", path_errors.len()),
                )
                .on_hover_text(path_errors.join("\n"));
            }

            ui.separator();
            ui.horizontal(|ui| {
                ui.add_enabled_ui(!busy, |ui| {
                    ui.checkbox(&mut self.skip_same, "Omitir iguales");
                    ui.checkbox(&mut self.keep_going, "Continuar con errores");
                    ui.checkbox(&mut self.verify, "Verificar BLAKE3");
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
                        let start_button = egui::Button::new(RichText::new("Iniciar copia").strong());
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
                        RichText::new("Analizando origen y destinos…")
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
                                0.0
                            } else {
                                d.written as f64 / d.total as f64
                            }
                        })
                        .sum::<f64>()
                        / snaps.len() as f64;
                    let total_bps: f64 = snaps.iter().map(|d| d.bps).sum();

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
                    ui.label(RichText::new("PROGRESO GLOBAL").strong());
                    ui.add(
                        egui::ProgressBar::new(avg_progress as f32)
                            .desired_height(18.0)
                            .show_percentage(),
                    );
                    ui.horizontal(|ui| {
                        ui.heading(format_bps(total_bps));
                        ui.weak(format!(
                            "{} · ETA {}",
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
                                .num_columns(5)
                                .spacing([12.0, 5.0])
                                .show(ui, |ui| {
                                    ui.strong("DESTINO");
                                    ui.strong("VELOCIDAD");
                                    ui.strong("PROGRESO");
                                    ui.strong("COLA");
                                    ui.strong("ESTADO");
                                    ui.end_row();
                                    for dp in &snaps {
                                        let frac = if dp.total == 0 {
                                            0.0
                                        } else {
                                            (dp.written as f32 / dp.total as f32).clamp(0.0, 1.0)
                                        };
                                        let (label, color) =
                                            phase_label(dp.phase, dp.files_err, self.use_light_theme);
                                        let path_response = ui.label(
                                            egui::RichText::new(compact_path(&dp.label, 34)).small(),
                                        );
                                        if dp.last_file.is_empty() {
                                            path_response.on_hover_text(&dp.label);
                                        } else {
                                            path_response.on_hover_text(format!(
                                                "{}\nArchivo: {}",
                                                dp.label, dp.last_file
                                            ));
                                        }
                                        ui.label(format_bps(dp.bps));
                                        ui.add(
                                            egui::ProgressBar::new(frac)
                                                .desired_width(150.0)
                                                .show_percentage(),
                                        );
                                        ui.label(dp.queue_depth.to_string());
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
                        ui.weak(format!(
                            "Buffers {}/{}",
                            job.buffers_in_flight.load(Ordering::Relaxed),
                            job.max_buffers
                        ));
                        ui.separator();
                        ui.weak(format!("{} destinos", snaps.len()));
                        ui.separator();
                        let retries: u64 = snaps.iter().map(|d| d.retries).sum();
                        ui.weak(format!("{} reintentos", retries));
                    });
                }
            } else {
                ui.add_space(SPACING_MD);
                ui.centered_and_justified(|ui| {
                    ui.label(RichText::new("Preparado para copiar").size(22.0).strong());
                });
            }
        });

        if self.show_credits {
            let mut open = true;
            egui::Window::new("Créditos")
                .collapsible(false)
                .resizable(false)
                .default_width(360.0)
                .open(&mut open)
                .show(ctx, |ui| {
                    ui.vertical_centered(|ui| {
                        ui.heading(RichText::new("DISK DUPLICATOR").strong());
                        ui.weak(format!("Versión {}", env!("CARGO_PKG_VERSION")));
                        ui.add_space(SPACING_MD);
                        ui.label("Desarrollado por ReinierTutoriales");
                        ui.add_space(SPACING_SM);
                        ui.weak("Rust · egui/eframe · BLAKE3");
                        ui.weak("Copiador de archivos 1 origen → N destinos HDD/SSD");
                        ui.add_space(SPACING_MD);
                        ui.hyperlink_to(
                            "github.com/ReinierTutoriales/disk-duplicator",
                            "https://github.com/ReinierTutoriales/disk-duplicator",
                        );
                        ui.add_space(SPACING_SM);
                        ui.weak("Licencia MIT");
                    });
                });
            self.show_credits = open;
        }

        if !busy
            && !snaps.is_empty()
            && snaps.iter().all(|d| {
                matches!(
                    d.phase,
                    DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled
                )
            })
        {
            let ok = snaps
                .iter()
                .filter(|d| d.phase == DestPhase::Done && d.files_err == 0)
                .count();
            let with_errors = snaps.iter().filter(|d| d.files_err > 0).count();
            self.status = if with_errors == 0 {
                format!("Completado · {ok}/{} destinos sin errores", snaps.len())
            } else {
                format!(
                    "Completado · {ok}/{} sin errores · {with_errors} con incidencias",
                    snaps.len()
                )
            };
        }
    }
}
