use crate::engine::{format_bps, start_job, CopyMode, CopyOpts, DestPhase, JobState};
use eframe::egui::{self, Color32, RichText};
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::Duration;

fn format_bytes(bytes: u64) -> String {
    const KIB: f64 = 1024.0;
    const MIB: f64 = KIB * 1024.0;
    const GIB: f64 = MIB * 1024.0;
    let b = bytes as f64;
    if b >= GIB { format!("{:.2} GiB", b / GIB) }
    else if b >= MIB { format!("{:.1} MiB", b / MIB) }
    else if b >= KIB { format!("{:.1} KiB", b / KIB) }
    else { format!("{} B", bytes) }
}

fn format_duration(secs: f64) -> String {
    if !secs.is_finite() || secs <= 0.0 { return "—".into(); }
    let total = secs.round() as u64;
    let h = total / 3600;
    let m = (total % 3600) / 60;
    let s = total % 60;
    if h > 0 { format!("{:02}:{:02}:{:02}", h, m, s) }
    else { format!("{:02}:{:02}", m, s) }
}

fn phase_label(p: DestPhase) -> (&'static str, Color32) {
    match p {
        DestPhase::Idle => ("EN ESPERA", Color32::from_gray(150)),
        DestPhase::Copying => ("COPIANDO", Color32::from_rgb(90, 170, 255)),
        DestPhase::Verifying => ("VERIFICANDO", Color32::from_rgb(180, 130, 255)),
        DestPhase::Done => ("COMPLETO", Color32::from_rgb(80, 210, 140)),
        DestPhase::Failed => ("ERROR", Color32::from_rgb(255, 95, 95)),
        DestPhase::Cancelled => ("CANCELADO", Color32::from_gray(130)),
    }
}

fn mode_label(mode: CopyMode) -> &'static str {
    match mode {
        CopyMode::Fanout => "FAN-OUT",
        CopyMode::PerDestination => "DESTINO",
        CopyMode::Fallback => "FALLBACK",
    }
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
        }
    }

    fn busy(&self) -> bool {
        self.job.as_ref().is_some_and(|j| j.running.load(Ordering::Relaxed))
            || self.workers.iter().any(|h| !h.is_finished())
    }

    fn pick_dir() -> Option<String> {
        rfd::FileDialog::new().pick_folder().map(|p| p.to_string_lossy().into_owned())
    }

    fn start(&mut self) {
        let src = PathBuf::from(self.source.trim());
        let dests: Vec<PathBuf> = self.dests.iter()
            .map(|s| PathBuf::from(s.trim()))
            .filter(|p| !p.as_os_str().is_empty())
            .collect();
        let opts = CopyOpts { verify: self.verify, skip_same: self.skip_same, keep_going: self.keep_going };
        match start_job(src, dests, opts) {
            Ok((state, handles)) => {
                self.status = format!("{} archivos · {} · {} destinos",
                    state.files_total.load(Ordering::Relaxed),
                    format_bytes(state.bytes_total.load(Ordering::Relaxed)),
                    state.dests.lock().map(|d| d.len()).unwrap_or(0));
                self.job = Some(state);
                self.workers = handles;
            }
            Err(e) => self.status = e,
        }
    }
}

impl eframe::App for CopierApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        ctx.set_visuals(egui::Visuals::dark());
        ctx.style_mut(|s| {
            s.spacing.item_spacing = egui::vec2(8.0, 6.0);
            s.spacing.button_padding = egui::vec2(10.0, 5.0);
        });

        let busy = self.busy();
        if busy { ctx.request_repaint_after(Duration::from_millis(120)); }

        egui::TopBottomPanel::top("header").show(ctx, |ui| {
            ui.add_space(5.0);
            ui.horizontal(|ui| {
                ui.heading(RichText::new("DISK DUPLICATOR").strong());
                ui.weak("1 → N  ·  HDD / SSD");
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if let Some(job) = &self.job {
                        ui.weak(format!("buffers {}/{}", job.buffers_in_flight.load(Ordering::Relaxed), job.max_buffers));
                    }
                });
            });
            ui.add_space(5.0);
        });

        egui::TopBottomPanel::bottom("footer").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.weak(&self.status);
                if let Some(job) = &self.job {
                    ui.separator();
                    ui.weak(if job.fanout { "fan-out activo" } else { "modo por destino" });
                }
            });
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.label(RichText::new("ORIGEN").strong());
                ui.add_enabled_ui(!busy, |ui| {
                    ui.add(egui::TextEdit::singleline(&mut self.source)
                        .desired_width(ui.available_width() - 90.0)
                        .hint_text("Carpeta origen"));
                    if ui.button("Examinar").clicked() {
                        if let Some(p) = Self::pick_dir() { self.source = p; }
                    }
                });
            });

            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.label(RichText::new(format!("DESTINOS  ({})", self.dests.len())).strong());
                ui.add_enabled_ui(!busy, |ui| {
                    if ui.button("+ Agregar").clicked() {
                        if let Some(p) = Self::pick_dir() {
                            if !self.dests.contains(&p) { self.dests.push(p); }
                        }
                    }
                });
            });

            egui::ScrollArea::vertical().id_salt("destinations").max_height(150.0).show(ui, |ui| {
                let mut remove = None;
                for (i, d) in self.dests.iter().enumerate() {
                    ui.horizontal(|ui| {
                        ui.weak(format!("{:02}", i + 1));
                        ui.label(d);
                        if !busy && ui.small_button("×").clicked() { remove = Some(i); }
                    });
                }
                if let Some(i) = remove { self.dests.remove(i); }
            });

            ui.separator();
            ui.horizontal(|ui| {
                ui.add_enabled_ui(!busy, |ui| {
                    ui.checkbox(&mut self.skip_same, "Omitir iguales");
                    ui.checkbox(&mut self.keep_going, "Continuar con errores");
                    ui.checkbox(&mut self.verify, "Verificar BLAKE3");
                });
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if let Some(job) = &self.job {
                        let paused = job.pause.load(Ordering::Relaxed);
                        if ui.button(if paused { "Continuar" } else { "Pausar" }).clicked() {
                            job.pause.store(!paused, Ordering::Relaxed);
                        }
                        if ui.button("Cancelar").clicked() {
                            job.cancel.store(true, Ordering::Relaxed);
                            job.pause.store(false, Ordering::Relaxed);
                        }
                    }
                    if !busy && ui.button(RichText::new("Iniciar copia").strong()).clicked() { self.start(); }
                });
            });

            if let Some(job) = &self.job {
                let snaps = job.snapshot();
                if !snaps.is_empty() {
                    let avg_progress = snaps.iter().map(|d| if d.total == 0 { 0.0 } else { d.written as f64 / d.total as f64 }).sum::<f64>() / snaps.len() as f64;
                    let total_bps: f64 = snaps.iter().map(|d| d.bps).sum();
                    let remaining = snaps.iter().map(|d| d.total.saturating_sub(d.written)).sum::<u64>() as f64;
                    let eta = if total_bps > 0.0 { remaining / total_bps } else { 0.0 };

                    ui.add_space(8.0);
                    ui.label(RichText::new("PROGRESO GLOBAL").strong());
                    ui.add(egui::ProgressBar::new(avg_progress as f32).desired_height(18.0));
                    ui.horizontal(|ui| {
                        ui.heading(format_bps(total_bps));
                        ui.weak(format!("{} · ETA {}", format_bytes(job.bytes_total.load(Ordering::Relaxed)), format_duration(eta)));
                    });

                    ui.add_space(6.0);
                    egui::Grid::new("dest-grid").striped(true).num_columns(6).spacing([12.0, 5.0]).show(ui, |ui| {
                        ui.strong("DESTINO"); ui.strong("VELOCIDAD"); ui.strong("PROGRESO"); ui.strong("COLA"); ui.strong("ESTADO"); ui.strong("MODO"); ui.end_row();
                        for dp in snaps {
                            let frac = if dp.total == 0 { 0.0 } else { (dp.written as f32 / dp.total as f32).clamp(0.0, 1.0) };
                            let (label, color) = phase_label(dp.phase);
                            ui.label(egui::RichText::new(&dp.label).small());
                            ui.label(format_bps(dp.bps));
                            ui.add(egui::ProgressBar::new(frac).desired_width(150.0).show_percentage());
                            ui.label(if job.fanout { dp.queue_depth.to_string() } else { "—".into() });
                            ui.colored_label(color, label);
                            ui.weak(mode_label(dp.mode));
                            ui.end_row();
                        }
                    });

                    ui.add_space(6.0);
                    ui.horizontal(|ui| {
                        ui.weak(format!("Buffers {}/{}", job.buffers_in_flight.load(Ordering::Relaxed), job.max_buffers));
                        ui.separator();
                        ui.weak(format!("{} destinos", job.dests.lock().map(|d| d.len()).unwrap_or(0)));
                        ui.separator();
                        let retries: u64 = job.snapshot().iter().map(|d| d.retries).sum();
                        ui.weak(format!("{} reintentos", retries));
                    });
                }
            } else {
                ui.add_space(12.0);
                ui.centered_and_justified(|ui| { ui.label(RichText::new("Preparado para copiar").size(22.0).strong()); });
            }
        });

        if !busy {
            if let Some(job) = &self.job {
                let snaps = job.snapshot();
                if !snaps.is_empty() && snaps.iter().all(|d| matches!(d.phase, DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled)) {
                    let ok = snaps.iter().filter(|d| d.phase == DestPhase::Done && d.files_err == 0).count();
                    self.status = format!("Completado · {ok}/{} destinos sin errores", snaps.len());
                }
            }
        }
    }
}
