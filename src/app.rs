use crate::disks::{enumerate_disks, DiskInfo};
use crate::engine::{format_bps, start_job, DestPhase, JobState};
use eframe::egui::{self, Color32, RichText, Stroke};
use std::sync::Arc;
use std::thread::JoinHandle;

const ACCENT: Color32 = Color32::from_rgb(88, 156, 196);
const MUTED: Color32 = Color32::from_rgb(140, 148, 152);
const BG: Color32 = Color32::from_rgb(22, 24, 26);
const PANEL: Color32 = Color32::from_rgb(32, 34, 38);

pub struct DuplicatorApp {
    disks: Vec<DiskInfo>,
    enum_error: Option<String>,
    source: Option<u32>,
    selected: Vec<bool>,
    confirm_serial: String,
    hide_system: bool,
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
        let selected = vec![false; disks.len()];
        Self {
            disks, enum_error, source: None, selected,
            confirm_serial: String::new(), hide_system: true,
            status: "Selecciona origen (referencia) y destinos.".into(),
            job: None, workers: Vec::new(),
        }
    }

    fn refresh(&mut self) {
        if self.job.as_ref().is_some_and(|j| j.running.load(std::sync::atomic::Ordering::Relaxed)) { return; }
        match enumerate_disks() {
            Ok(d) => { self.disks = d; self.selected.resize(self.disks.len(), false); self.enum_error = None; self.status = "Lista actualizada.".into(); }
            Err(e) => self.enum_error = Some(e),
        }
    }

    fn source_disk(&self) -> Option<&DiskInfo> {
        let idx = self.source?;
        self.disks.iter().find(|d| d.index == idx)
    }

    fn dest_disks(&self) -> Vec<DiskInfo> {
        self.disks.iter().zip(self.selected.iter()).filter_map(|(d, on)| {
            if *on && Some(d.index) != self.source && !d.is_system { Some(d.clone()) } else { None }
        }).collect()
    }

    fn confirm_ok(&self, src: &DiskInfo) -> bool {
        let tail: String = src.serial.chars().filter(|c| c.is_ascii_alphanumeric()).collect::<String>().to_uppercase();
        let typed: String = self.confirm_serial.chars().filter(|c| c.is_ascii_alphanumeric()).collect::<String>().to_uppercase();
        if tail.len() < 4 { return typed == tail || (typed.len() >= 3 && tail.contains(&typed)); }
        typed == tail[tail.len()-4..] || typed == tail
    }

    fn problems(&self) -> Vec<String> {
        let mut p = Vec::new();
        let Some(src) = self.source_disk() else { p.push("No hay origen.".into()); return p; };
        if src.is_system { p.push("El origen es el disco de sistema.".into()); }
        let dests = self.dest_disks();
        if dests.is_empty() { p.push("No hay destinos válidos.".into()); }
        for d in &dests {
            if d.size_bytes < src.size_bytes {
                p.push(format!("PD{} es más pequeño que el origen ({} GB < {} GB).", d.index, d.size_gb(), src.size_gb()));
            }
        }
        if !self.confirm_ok(src) {
            p.push("Escribe las últimas 4 del serial del ORIGEN para habilitar Iniciar.".into());
        }
        p
    }

    fn start(&mut self) {
        let problems = self.problems();
        if !problems.is_empty() { self.status = problems.join(" "); return; }
        let src = self.source_disk().unwrap().clone();
        let dests = self.dest_disks();
        self.status = format!("Copiando PD{} → {} destino(s). Sesiones independientes.", src.index, dests.len());
        let (state, handles) = start_job(src, dests);
        self.job = Some(state);
        self.workers = handles;
    }

    fn cancel(&mut self) {
        if let Some(j) = &self.job { j.cancel.store(true, std::sync::atomic::Ordering::Relaxed); }
        self.status = "Cancelando…".into();
    }
}

impl eframe::App for DuplicatorApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        apply_obsidian(ctx);
        if self.job.as_ref().is_some_and(|j| j.running.load(std::sync::atomic::Ordering::Relaxed)) {
            ctx.request_repaint_after(std::time::Duration::from_millis(100));
        }
        egui::TopBottomPanel::top("top").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label(RichText::new("DISK DUPLICATOR").strong().size(18.0).color(ACCENT));
                ui.label(RichText::new("  1 origen → N destinos").color(MUTED));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button("Actualizar discos").clicked() { self.refresh(); }
                    ui.checkbox(&mut self.hide_system, "Ocultar sistema");
                });
            });
        });
        egui::TopBottomPanel::bottom("bot").show(ctx, |ui| {
            ui.label(RichText::new(&self.status).color(Color32::from_rgb(200, 200, 196)));
            if let Some(e) = &self.enum_error { ui.colored_label(Color32::from_rgb(220, 80, 80), e); }
        });
        egui::SidePanel::right("side").min_width(320.0).show(ctx, |ui| {
            ui.heading("Selección");
            ui.separator();
            if let Some(s) = self.source_disk() {
                ui.label(RichText::new("Origen").color(MUTED));
                ui.label(RichText::new(format!("PD{}  {}", s.index, s.model)).strong());
                ui.label(format!("{} GB   serial {}", s.size_gb(), s.serial));
            } else { ui.label("Sin origen."); }
            ui.add_space(8.0);
            ui.label(RichText::new("Destinos").color(MUTED));
            let dests = self.dest_disks();
            if dests.is_empty() { ui.label("Ninguno."); }
            else { for d in &dests { ui.label(format!("PD{}  {}  {} GB", d.index, d.model, d.size_gb())); } }
            ui.add_space(8.0);
            ui.label(RichText::new("Validación").color(MUTED));
            let probs = self.problems();
            if probs.is_empty() {
                ui.colored_label(Color32::from_rgb(120, 190, 120), "Listo para copiar.");
                ui.label("Se destruyen todos los datos de los destinos.");
            } else {
                for p in probs { ui.colored_label(Color32::from_rgb(220, 160, 80), format!("• {p}")); }
            }
            ui.add_space(8.0);
            ui.label("Últimas 4 del serial del origen:");
            ui.text_edit_singleline(&mut self.confirm_serial);
            ui.add_space(8.0);
            ui.horizontal(|ui| {
                let can = self.problems().is_empty() && !self.job.as_ref().is_some_and(|j| j.running.load(std::sync::atomic::Ordering::Relaxed));
                ui.add_enabled_ui(can, |ui| {
                    if ui.add(egui::Button::new(RichText::new("Iniciar").strong()).fill(ACCENT)).clicked() { self.start(); }
                });
                if ui.button("Cancelar").clicked() { self.cancel(); }
            });
        });
        egui::CentralPanel::default().show(ctx, |ui| {
            ui.heading("Discos físicos");
            egui::Grid::new("disks").striped(true).show(ui, |ui| {
                ui.label(RichText::new("Origen").color(MUTED));
                ui.label(RichText::new("Destino").color(MUTED));
                ui.label(RichText::new("Disco").color(MUTED));
                ui.label(RichText::new("Modelo").color(MUTED));
                ui.label(RichText::new("Vol").color(MUTED));
                ui.label(RichText::new("Tamaño").color(MUTED));
                ui.label(RichText::new("Bus").color(MUTED));
                ui.end_row();
                for i in 0..self.disks.len() {
                    let d = &self.disks[i];
                    if self.hide_system && d.is_system { continue; }
                    let sys = d.is_system;
                    let is_src = self.source == Some(d.index);
                    ui.add_enabled_ui(!sys, |ui| {
                        if ui.radio(is_src, "").clicked() {
                            self.source = Some(d.index);
                            if i < self.selected.len() { self.selected[i] = false; }
                        }
                    });
                    ui.add_enabled_ui(!sys && !is_src, |ui| {
                        if i < self.selected.len() { ui.checkbox(&mut self.selected[i], ""); }
                    });
                    ui.label(format!("PD{}", d.index));
                    ui.label(&d.model);
                    let vols = if d.letters.is_empty() { "—".into() } else { d.letters.iter().map(|c| format!("{c}:")).collect::<Vec<_>>().join(" ") };
                    ui.label(if sys { format!("{vols} SYS") } else { vols });
                    ui.label(format!("{} GB", d.size_gb()));
                    ui.label(d.bus.label());
                    ui.end_row();
                }
            });
            if let Some(job) = &self.job {
                ui.add_space(12.0);
                ui.heading("Progreso por destino");
                for dp in job.snapshot() {
                    let frac = if dp.total == 0 { 0.0 } else { dp.written as f32 / dp.total as f32 };
                    let phase = match dp.phase {
                        DestPhase::Idle => "en cola",
                        DestPhase::Locking => "bloqueando",
                        DestPhase::Copying => "escribiendo",
                        DestPhase::Done => "ok",
                        DestPhase::Failed => "error",
                        DestPhase::Cancelled => "cancelado",
                    };
                    ui.horizontal(|ui| {
                        ui.label(format!("PD{}", dp.index));
                        ui.add(egui::ProgressBar::new(frac).desired_width(280.0).show_percentage());
                        ui.label(format_bps(dp.bps));
                        ui.label(phase);
                    });
                    if let Some(e) = dp.error { ui.colored_label(Color32::from_rgb(220, 80, 80), e); }
                }
            }
        });
    }
}

fn apply_obsidian(ctx: &egui::Context) {
    let mut style = (*ctx.style()).clone();
    style.visuals.dark_mode = true;
    style.visuals.panel_fill = PANEL;
    style.visuals.window_fill = PANEL;
    style.visuals.extreme_bg_color = BG;
    style.visuals.faint_bg_color = Color32::from_rgb(40, 42, 46);
    style.visuals.override_text_color = Some(Color32::from_rgb(220, 222, 224));
    style.visuals.widgets.inactive.bg_fill = Color32::from_rgb(48, 50, 54);
    style.visuals.widgets.hovered.bg_fill = Color32::from_rgb(60, 70, 80);
    style.visuals.widgets.active.bg_fill = ACCENT;
    style.visuals.selection.bg_fill = Color32::from_rgb(50, 90, 120);
    style.visuals.widgets.noninteractive.bg_stroke = Stroke::NONE;
    ctx.set_style(style);
}
