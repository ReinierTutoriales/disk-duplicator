use crate::engine::{format_bps, format_bytes, start_job, DestPhase, JobState};
use eframe::egui::{self, Align, Color32, Layout, RichText};
use std::path::PathBuf;
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

pub struct CopierApp {
    source: String,
    dests: Vec<String>,
    verify: bool,
    status: String,
    job: Option<Arc<JobState>>,
    workers: Vec<JoinHandle<()>>,
}

impl CopierApp {
    pub fn new() -> Self {
        Self {
            source: String::new(),
            dests: Vec::new(),
            verify: true,
            status: "Carpeta origen → varias carpetas destino. No clona discos.".into(),
            job: None,
            workers: Vec::new(),
        }
    }

    fn busy(&self) -> bool {
        self.job
            .as_ref()
            .is_some_and(|j| j.running.load(std::sync::atomic::Ordering::Relaxed))
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
        match start_job(src, dests, self.verify) {
            Ok((state, handles)) => {
                self.status = format!(
                    "Copiando {} archivos, {}.",
                    state.files_total.load(std::sync::atomic::Ordering::Relaxed),
                    format_bytes(state.bytes_total.load(std::sync::atomic::Ordering::Relaxed))
                );
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
        }

        egui::TopBottomPanel::top("top").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.strong("Copiador");
                ui.weak("1 carpeta → N destinos");
                ui.checkbox(&mut self.verify, "Releer y comparar hash");
            });
        });

        egui::TopBottomPanel::bottom("bot").show(ctx, |ui| {
            ui.label(&self.status);
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label("Origen");
                ui.add(
                    egui::TextEdit::singleline(&mut self.source)
                        .desired_width(520.0)
                        .hint_text("Carpeta a copiar"),
                );
                if ui.button("Examinar").clicked() {
                    if let Some(p) = Self::pick_dir() {
                        self.source = p;
                    }
                }
            });

            ui.add_space(6.0);
            ui.horizontal(|ui| {
                ui.label("Destinos");
                if ui.button("Agregar").clicked() {
                    if let Some(p) = Self::pick_dir() {
                        if !self.dests.contains(&p) {
                            self.dests.push(p);
                        }
                    }
                }
            });

            let mut remove = None;
            for (i, d) in self.dests.iter().enumerate() {
                ui.horizontal(|ui| {
                    ui.label(d);
                    if ui.small_button("Quitar").clicked() {
                        remove = Some(i);
                    }
                });
            }
            if let Some(i) = remove {
                self.dests.remove(i);
            }

            ui.add_space(8.0);
            ui.horizontal(|ui| {
                ui.add_enabled_ui(!self.busy(), |ui| {
                    if ui.button(RichText::new("Iniciar").strong()).clicked() {
                        self.start();
                    }
                });
                if let Some(job) = &self.job {
                    let paused = job.pause.load(std::sync::atomic::Ordering::Relaxed);
                    if ui.button(if paused { "Seguir" } else { "Pausar" }).clicked() {
                        job.pause.store(!paused, std::sync::atomic::Ordering::Relaxed);
                    }
                    if ui.button("Cancelar").clicked() {
                        job.cancel.store(true, std::sync::atomic::Ordering::Relaxed);
                        job.pause.store(false, std::sync::atomic::Ordering::Relaxed);
                        self.status = "Cancelando…".into();
                    }
                }
            });

            if let Some(job) = &self.job {
                ui.add_space(10.0);
                ui.separator();
                let total_files = job.files_total.load(std::sync::atomic::Ordering::Relaxed);
                for dp in job.snapshot() {
                    let frac = if dp.total == 0 {
                        0.0
                    } else {
                        dp.written as f32 / dp.total as f32
                    };
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
                        ui.add(
                            egui::ProgressBar::new(frac)
                                .desired_width(360.0)
                                .show_percentage(),
                        );
                        ui.label(format_bps(dp.bps));
                        ui.colored_label(phase_color(dp.phase), tag);
                        ui.weak(format!("{}/{} archivos", dp.files_done, total_files));
                    });
                    if !dp.last_file.is_empty() {
                        ui.label(RichText::new(&dp.last_file).small().weak());
                    }
                    if let Some(e) = dp.error {
                        ui.colored_label(Color32::from_rgb(200, 60, 60), e);
                    }
                }
            }
        });
        let _ = Align::Min;
        let _ = Layout::left_to_right(Align::Min);
    }
}
