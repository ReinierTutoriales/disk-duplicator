use crate::disks::{enumerate_disks, DiskInfo};
use crate::engine::{format_bps, start_job, DestPhase, JobState};
use eframe::egui::{self, Align, Color32, Layout, RichText};
use std::sync::Arc;
use std::thread::JoinHandle;

pub struct DuplicatorApp {
    disks: Vec<DiskInfo>,
    enum_error: Option<String>,
    source: Option<u32>,
    selected: Vec<bool>,
    hide_system: bool,
    verify: bool,
    confirm_open: bool,
    status: String,
    job: Option<Arc<JobState>>,
    workers: Vec<JoinHandle<()>>,
}

impl DuplicatorApp {
    pub fn new() -> Self {
        let (disks, enum_error) = match enumerate_disks() {
            Ok(d) => (d, None),
            Err(e) => (Vec::new(), Some(e)),
        };
        let n = disks.len();
        Self {
            disks,
            enum_error,
            source: None,
            selected: vec![false; n],
            hide_system: true,
            verify: true,
            confirm_open: false,
            status: "Origen con el círculo. Destinos con el check. Cierra Explorer en los destinos.".into(),
            job: None,
            workers: Vec::new(),
        }
    }

    fn busy(&self) -> bool {
        self.job
            .as_ref()
            .is_some_and(|j| j.running.load(std::sync::atomic::Ordering::Relaxed))
    }

    fn refresh(&mut self) {
        if self.busy() {
            return;
        }
        match enumerate_disks() {
            Ok(d) => {
                self.disks = d;
                self.selected = vec![false; self.disks.len()];
                self.source = None;
                self.enum_error = None;
                self.status = format!("{} disco(s).", self.disks.len());
            }
            Err(e) => self.enum_error = Some(e),
        }
    }

    fn source_disk(&self) -> Option<&DiskInfo> {
        let idx = self.source?;
        self.disks.iter().find(|d| d.index == idx)
    }

    fn dest_disks(&self) -> Vec<DiskInfo> {
        self.disks
            .iter()
            .zip(self.selected.iter())
            .filter_map(|(d, on)| {
                if *on && Some(d.index) != self.source && !d.is_system {
                    Some(d.clone())
                } else {
                    None
                }
            })
            .collect()
    }

    fn problems(&self) -> Vec<String> {
        let mut p = Vec::new();
        let Some(src) = self.source_disk() else {
            p.push("Elige origen.".into());
            return p;
        };
        if src.is_system {
            p.push("El origen es el disco de Windows.".into());
        }
        let dests = self.dest_disks();
        if dests.is_empty() {
            p.push("Elige al menos un destino.".into());
        }
        for d in &dests {
            if d.size_bytes < src.size_bytes {
                p.push(format!(
                    "PD{} ({:.1} GB) es más chico que el origen ({:.1} GB).",
                    d.index,
                    d.size_gb(),
                    src.size_gb()
                ));
            }
        }
        p
    }

    fn start(&mut self) {
        let problems = self.problems();
        if !problems.is_empty() {
            self.status = problems.join(" ");
            self.confirm_open = false;
            return;
        }
        let src = self.source_disk().unwrap().clone();
        let dests = self.dest_disks();
        self.status = format!(
            "PD{} → {} destino(s).{}",
            src.index,
            dests.len(),
            if self.verify { " Verificando CRC." } else { "" }
        );
        let (state, handles) = start_job(src, dests, self.verify);
        self.job = Some(state);
        self.workers = handles;
        self.confirm_open = false;
    }
}

impl eframe::App for DuplicatorApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        ctx.style_mut(|s| {
            s.spacing.item_spacing = egui::vec2(6.0, 4.0);
            s.spacing.button_padding = egui::vec2(8.0, 3.0);
        });
        if self.busy() {
            ctx.request_repaint_after(std::time::Duration::from_millis(120));
        }

        egui::TopBottomPanel::top("top").exact_height(28.0).show(ctx, |ui| {
            ui.horizontal_centered(|ui| {
                ui.strong("Disk Duplicator");
                ui.separator();
                ui.checkbox(&mut self.hide_system, "Ocultar sistema");
                ui.checkbox(&mut self.verify, "Verificar CRC");
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    if ui.button("Actualizar").clicked() {
                        self.refresh();
                    }
                });
            });
        });

        egui::TopBottomPanel::bottom("bot").show(ctx, |ui| {
            ui.label(&self.status);
            if let Some(e) = &self.enum_error {
                ui.colored_label(Color32::from_rgb(200, 60, 60), e);
            }
        });

        egui::SidePanel::right("side").exact_width(280.0).show(ctx, |ui| {
            ui.add_space(4.0);
            if let Some(s) = self.source_disk() {
                ui.label(RichText::new("Origen").small().weak());
                ui.label(format!("PD{}  {}", s.index, s.model));
                ui.label(RichText::new(format!("{:.1} GB  {}", s.size_gb(), s.serial)).small());
            } else {
                ui.weak("Sin origen");
            }
            ui.add_space(6.0);
            ui.label(RichText::new("Destinos").small().weak());
            let dests = self.dest_disks();
            if dests.is_empty() {
                ui.weak("Ninguno");
            } else {
                for d in &dests {
                    ui.label(format!("PD{}  {:.1} GB  {}", d.index, d.size_gb(), d.model));
                }
            }
            ui.add_space(8.0);
            let probs = self.problems();
            if probs.is_empty() {
                ui.colored_label(Color32::from_rgb(40, 140, 70), "Listo.");
                ui.label(RichText::new("Iniciar borra los destinos.").small());
            } else {
                for p in &probs {
                    ui.colored_label(Color32::from_rgb(170, 110, 30), p);
                }
            }
            ui.add_space(10.0);
            ui.horizontal(|ui| {
                let can = probs.is_empty() && !self.busy();
                ui.add_enabled_ui(can, |ui| {
                    if ui.button(RichText::new("Iniciar").strong()).clicked() {
                        self.confirm_open = true;
                    }
                });
                if ui.button("Cancelar").clicked() {
                    if let Some(j) = &self.job {
                        j.cancel.store(true, std::sync::atomic::Ordering::Relaxed);
                    }
                    self.status = "Cancelando…".into();
                }
            });
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            egui::Grid::new("disks").striped(true).num_columns(7).show(ui, |ui| {
                for h in ["Ori", "Dst", "Disco", "Modelo", "Vol", "GB", "Bus"] {
                    ui.label(RichText::new(h).small().weak());
                }
                ui.end_row();
                for i in 0..self.disks.len() {
                    let d = &self.disks[i];
                    if self.hide_system && d.is_system {
                        continue;
                    }
                    let sys = d.is_system;
                    let is_src = self.source == Some(d.index);
                    ui.add_enabled_ui(!sys && !self.busy(), |ui| {
                        if ui.radio(is_src, "").clicked() {
                            self.source = Some(d.index);
                            if i < self.selected.len() {
                                self.selected[i] = false;
                            }
                        }
                    });
                    ui.add_enabled_ui(!sys && !is_src && !self.busy(), |ui| {
                        if i < self.selected.len() {
                            ui.checkbox(&mut self.selected[i], "");
                        }
                    });
                    ui.label(format!("PD{}", d.index));
                    ui.label(&d.model);
                    let vols = if d.letters.is_empty() {
                        "—".into()
                    } else {
                        d.letters.iter().map(|c| format!("{c}:")).collect::<Vec<_>>().join(" ")
                    };
                    ui.label(if sys { format!("{vols} SYS") } else { vols });
                    ui.label(format!("{:.1}", d.size_gb()));
                    ui.label(d.bus.label());
                    ui.end_row();
                }
            });

            if let Some(job) = &self.job {
                ui.add_space(8.0);
                ui.separator();
                for dp in job.snapshot() {
                    let (frac, tag) = match dp.phase {
                        DestPhase::Verifying => (
                            if dp.total == 0 {
                                0.0
                            } else {
                                dp.verified as f32 / dp.total as f32
                            },
                            "verificando",
                        ),
                        _ => (
                            if dp.total == 0 {
                                0.0
                            } else {
                                dp.written as f32 / dp.total as f32
                            },
                            match dp.phase {
                                DestPhase::Idle => "en cola",
                                DestPhase::Locking => "bloqueando",
                                DestPhase::Copying => "escribiendo",
                                DestPhase::Done => "ok",
                                DestPhase::Failed => "error",
                                DestPhase::Cancelled => "cancelado",
                                DestPhase::Verifying => "verificando",
                            },
                        ),
                    };
                    ui.horizontal(|ui| {
                        ui.label(format!("PD{}", dp.index));
                        ui.add(egui::ProgressBar::new(frac).desired_width(260.0).show_percentage());
                        ui.label(format_bps(dp.bps));
                        ui.label(tag);
                    });
                    if let Some(e) = dp.error {
                        ui.colored_label(Color32::from_rgb(200, 60, 60), e);
                    }
                }
            }
        });

        if self.confirm_open {
            let dests = self.dest_disks();
            let src = self.source_disk().map(|s| s.label()).unwrap_or_default();
            egui::Window::new("Confirmar escritura")
                .collapsible(false)
                .resizable(false)
                .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
                .show(ctx, |ui| {
                    ui.label("Se va a borrar por completo:");
                    for d in &dests {
                        ui.strong(d.label());
                    }
                    ui.add_space(6.0);
                    ui.label(format!("Origen: {src}"));
                    if self.verify {
                        ui.label("Después se verifica CRC32 del destino.");
                    }
                    ui.add_space(8.0);
                    ui.horizontal(|ui| {
                        if ui.button("Cancelar").clicked() {
                            self.confirm_open = false;
                        }
                        if ui
                            .button(RichText::new("Escribir destinos").color(Color32::WHITE).strong())
                            .clicked()
                        {
                            self.start();
                        }
                    });
                });
        }
    }
}
