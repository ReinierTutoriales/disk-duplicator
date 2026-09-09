use crate::engine::{format_bps, start_job, CopyMode, CopyOpts, DestPhase, JobState};
use eframe::egui::{self, Color32, RichText};
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};
use std::time::Duration;

type StartResult = Result<(Arc<JobState>, Vec<JoinHandle<()>>), String>;

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

fn phase_label(p: DestPhase, files_err: u64) -> (&'static str, Color32) {
    match p {
        DestPhase::Idle => ("EN ESPERA", Color32::from_gray(150)),
        DestPhase::Copying => ("COPIANDO", Color32::from_rgb(90, 170, 255)),
        DestPhase::Verifying => ("VERIFICANDO", Color32::from_rgb(180, 130, 255)),
        DestPhase::Done if files_err > 0 => ("CON ERRORES", Color32::from_rgb(235, 175, 70)),
        DestPhase::Done => ("COMPLETO", Color32::from_rgb(80, 210, 140)),
        DestPhase::Failed => ("ERROR", Color32::from_rgb(255, 95, 95)),
        DestPhase::Cancelled => ("CANCELADO", Color32::from_gray(130)),
    }
}

fn mode_label(mode: CopyMode) -> &'static str {
    match mode {
        CopyMode::Fanout => "FAN-OUT",
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

    fn start(&mut self) {
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
                self.status = format!(
                    "Preflight correcto · {} archivos · {} · {} destinos",
                    state.files_total.load(Ordering::Relaxed),
                    format_bytes(state.bytes_total.load(Ordering::Relaxed)),
                    state.dests.lock().map(|d| d.len()).unwrap_or(0)
                );
                self.job = Some(state);
                self.workers = handles;
            }
            Ok(Err(e)) | Err(e) => self.status = e,
        }
    }
}

impl eframe::App for CopierApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll_startup();

        ctx.set_visuals(egui::Visuals::dark());
        ctx.style_mut(|s| {
            s.spacing.item_spacing = egui::vec2(8.0, 6.0);
            s.spacing.button_padding = egui::vec2(10.0, 5.0);
        });

        let starting = self.starting();
        let running = self.running_job();
        let busy = starting || running;
        if busy {
            ctx.request_repaint_after(Duration::from_millis(120));
        }

        egui::TopBottomPanel::top("header").show(ctx, |ui| {
            ui.add_space(5.0);
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
            ui.add_space(5.0);
        });

        egui::TopBottomPanel::bottom("footer").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.weak(&self.status);
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
            ui.add_space(4.0);
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

            ui.add_space(4.0);
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
                            ui.label(compact_path(d, 76)).on_hover_text(d);
                            if !busy && ui.small_button("×").clicked() {
                                remove = Some(i);
                            }
                        });
                    }
                    if let Some(i) = remove {
                        self.dests.remove(i);
                    }
                });

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
                    } else if !starting
                        && ui.button(RichText::new("Iniciar copia").strong()).clicked()
                    {
                        self.start();
                    }
                    if starting {
                        ui.weak("Analizando…");
                    }
                });
            });

            if starting {
                ui.add_space(12.0);
                ui.centered_and_justified(|ui| {
                    ui.label(
                        RichText::new("Analizando origen y destinos…")
                            .size(20.0)
                            .strong(),
                    );
                });
            } else if let Some(job) = &self.job {
                let snaps = job.snapshot();
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

                    ui.add_space(8.0);
                    ui.label(RichText::new("PROGRESO GLOBAL").strong());
                    ui.add(egui::ProgressBar::new(avg_progress as f32).desired_height(18.0));
                    ui.horizontal(|ui| {
                        ui.heading(format_bps(total_bps));
                        ui.weak(format!(
                            "{} · ETA {}",
                            format_bytes(job.bytes_total.load(Ordering::Relaxed)),
                            eta_text
                        ));
                    });

                    ui.add_space(6.0);
                    egui::Grid::new("dest-grid")
                        .striped(true)
                        .num_columns(6)
                        .spacing([12.0, 5.0])
                        .show(ui, |ui| {
                            ui.strong("DESTINO");
                            ui.strong("VELOCIDAD");
                            ui.strong("PROGRESO");
                            ui.strong("COLA");
                            ui.strong("ESTADO");
                            ui.strong("MODO");
                            ui.end_row();
                            for dp in &snaps {
                                let frac = if dp.total == 0 {
                                    0.0
                                } else {
                                    (dp.written as f32 / dp.total as f32).clamp(0.0, 1.0)
                                };
                                let (label, color) = phase_label(dp.phase, dp.files_err);
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
                                ui.weak(mode_label(dp.mode));
                                ui.end_row();
                            }
                        });

                    ui.add_space(6.0);
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
                ui.add_space(12.0);
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
                        ui.add_space(10.0);
                        ui.label("Desarrollado por ReinierTutoriales");
                        ui.add_space(6.0);
                        ui.weak("Rust · egui/eframe · BLAKE3");
                        ui.weak("Copiador de archivos 1 origen → N destinos HDD/SSD");
                        ui.add_space(10.0);
                        ui.hyperlink_to(
                            "github.com/ReinierTutoriales/disk-duplicator",
                            "https://github.com/ReinierTutoriales/disk-duplicator",
                        );
                        ui.add_space(6.0);
                        ui.weak("Licencia MIT");
                    });
                });
            self.show_credits = open;
        }

        if !busy {
            if let Some(job) = &self.job {
                let snaps = job.snapshot();
                if !snaps.is_empty()
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
    }
}
