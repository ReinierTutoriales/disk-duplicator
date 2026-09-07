#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
#[path = "engine.rs"]
mod engine_impl;
mod preflight;

mod engine {
    pub use crate::engine_impl::{format_bps, CopyMode, CopyOpts, DestPhase, JobState};
    pub use crate::preflight::start_job;
}

use app::CopierApp;
use eframe::egui;

fn main() -> eframe::Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([920.0, 560.0])
            .with_min_inner_size([720.0, 420.0])
            .with_title("Copiador"),
        centered: true,
        ..Default::default()
    };
    eframe::run_native(
        "Copiador",
        options,
        Box::new(|_cc| Ok(Box::new(CopierApp::new()))),
    )
}
