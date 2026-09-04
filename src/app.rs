use crate::engine::{format_bps, format_bytes, start_job, CopyMode, CopyOpts, DestPhase, JobState};
use eframe::egui::{self, Color32, RichText};
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::thread::JoinHandle;

fn phase_color(p: DestPhase) -> Color32 {
    match p {
        DestPhase::Idle => Color32::from_gray(140),
        DestPhase::Copying => Color32::from_rgb(60, 120, 200),
        DestPhase::Verifying => Color32::from_rgb(150, 90, 200),
        DestPhase::Done => Color32::from_rgb(40, 140, 70),
        DestPhase::Failed => Color32::from_rgb(200, 60, 60),
        DestPhase::Cancelled => Color32::from_gray(150),
    }
}

fn mode_label(mode: CopyMode) -> &'static str {
    match mode {
        CopyMode::Fanout => "fan-out",
        CopyMode::PerDestination => "por destino",
        CopyMode::Fallback => "fallback",
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
            status: "Una carpeta origen, varios destinos.".into(),
            job: None,
            workers: Vec::new(),
        }
    }

    fn busy(&self) -> bool {
        self.job.as_ref().is_some_and(|j| j.running.load(Ordering::Relaxed))
    }

    fn pick_dir() -> Option<String> {
        rfd::FileDialog::new().pick_folder().map(|p| p.to_string_lossy().into_owned())
    }

    fn start(&mut self) {
        let src = PathBuf::from(self.source.trim());
        let dests: Vec<PathBuf> = self.dests.iter().map(|s| PathBuf::from(s.trim())).filter(|p| !p.as_os_str().is_empty()).collect();
        let opts = CopyOpts { verify: self.verify, skip_same: self.skip_same, keep_going: self.keep_going };
        match start_job(src, dests, opts) {
            Ok((state, handles)) => {
                let mode = if state.fanout { "fan-out" } else { "por destino" };
                self.status = format!("{} archivos · {} · modo {}", state.files_total.load(Ordering::Relaxed), format_bytes(state.bytes_total.load(Ordering::Relaxed)), mode);
                self.job = Some(state);
                self.workers = handles;
            }
            Err(e) => self.status = e,
        }
    }
}

impl eframe::App for CopierApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        ctx.style_mut(|s| {
            s.spacing.item_spacing = egui::vec2(6.0, 4.0);
            s.spacing.button_padding = egui::vec2(8.0, 3.0);
        });
        if self.busy() {
            ctx.request_repaint_after(std::time::Duration::from_millis(120));
        } else if let Some(job) = &self.job {
            let snaps = job.snapshot();
            if snaps.iter().all(|d| matches!(d.phase, DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled)) && !snaps.is_empty() {
                let ok = snaps.iter().filter(|d| d.phase == DestPhase::Done && d.files_err == 0).count();
                self.status = format!("Listo. {ok}/{} destinos sin errores.", snaps.len());
            }
        }

        egui::TopBottomPanel::top("top").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.strong("Copiador");
                ui.separator();
                ui.add_enabled_ui(!self.busy(), |ui| {
                    ui.checkbox(&mut self.skip_same, "Omitir iguales");
                    ui.checkbox(&mut self.keep_going, "Seguir si hay error");
                    ui.checkbox(&mut self.verify, "Verificar hash");
                });
            });
        });

        egui::TopBottomPanel::bottom("bot").show(ctx, |ui| {
            if let Some(job) = &self.job {
                ui.horizontal(|ui| {
                    ui.label(&self.status);
                    if job.fanout {
                        ui.separator();
                        ui.weak(format!("buffers {}/{}", job.buffers_in_flight.load(Ordering::Relaxed), job.max_buffers));
                    }
                });
            } else {
                ui.label(&self.status);
            }
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label("Origen");
                ui.add_enabled_ui(!self.busy(), |ui| {
                    ui.add(egui::TextEdit::singleline(&mut self.source).desired_width(500.0).hint_text("Carpeta a copiar"));
                    if ui.button("Examinar").clicked() {
                        if let Some(p) = Self::pick_dir() { self.source = p; }
                    }
                });
            });

            ui.horizontal(|ui| {
                ui.label("Destinos");
                ui.add_enabled_ui(!self.busy(), |ui| {
                    if ui.button("Agregar").clicked() {
                        if let Some(p) = Self::pick_dir() {
                            if !self.dests.contains(&p) { self.dests.push(p); }
                        }
                    }
                });
            });

            let mut remove = None;
            for (i, d) in self.dests.iter().enumerate() {
                ui.horizontal(|ui| {
                    ui.label(d);
                    ui.add_enabled_ui(!self.busy(), |ui| {
                        if ui.small_button("Quitar").clicked() { remove = Some(i); }
                    });
                });
            }
            if let Some(i) = remove { self.dests.remove(i); }

            ui.add_space(8.0);
            ui.horizontal(|ui| {
                ui.add_enabled_ui(!self.busy() && !self.source.is_empty() && !self.dests.is_empty(), |ui| {
                    if ui.button(RichText::new("Iniciar").strong()).clicked() { self.start(); }
                });
                if let Some(job) = &self.job {
                    let paused = job.pause.load(Ordering::Relaxed);
                    if ui.button(if paused { "Seguir" } else { "Pausar" }).clicked() { job.pause.store(!paused, Ordering::Relaxed); }
                    if ui.button("Cancelar").clicked() {
                        job.cancel.store(true, Ordering::Relaxed);
                        job.pause.store(false, Ordering::Relaxed);
                    }
                }
            });

            if let Some(job) = &self.job {
                ui.add_space(8.0);
                ui.separator();
                let total_files = job.files_total.load(Ordering::Relaxed);
                for dp in job.snapshot() {
                    let frac = if dp.total == 0 { 0.0 } else { (dp.written as f32 / dp.total as f32).clamp(0.0, 1.0) };
                    let tag = match dp.phase {
                        DestPhase::Idle => "en cola",
                        DestPhase::Copying => "copiando",
                        DestPhase::Verifying => "verificando",
                        DestPhase::Done => "ok",
                        DestPhase::Failed => "error",
                        DestPhase::Cancelled => "cancelado",
                    };
                    ui.label(RichText::new(&dp.label).small());
                    ui.horizontal(|ui| {
                        ui.add(egui::ProgressBar::new(frac).desired_width(330.0).show_percentage());
                        ui.label(format_bps(dp.bps));
                        ui.colored_label(phase_color(dp.phase), tag);
                        ui.weak(mode_label(dp.mode));
                        if job.fanout { ui.weak(format!("q {}", dp.queue_depth)); }
                        ui.weak(format!("{}/{}", dp.files_done, total_files));
                        if dp.files_skip > 0 { ui.weak(format!("omitidos {}", dp.files_skip)); }
                        if dp.retries > 0 { ui.weak(format!("reintentos {}", dp.retries)); }
                        if dp.files_err > 0 { ui.colored_label(Color32::from_rgb(200, 60, 60), format!("err {}", dp.files_err)); }
                    });
                    if !dp.last_file.is_empty() && dp.phase == DestPhase::Copying { ui.label(RichText::new(&dp.last_file).small().weak()); }
                    if let Some(e) = dp.error { ui.colored_label(Color32::from_rgb(200, 60, 60), e); }
                }
            }
        });
    }
}
