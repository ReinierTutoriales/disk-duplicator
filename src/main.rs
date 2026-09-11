#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod config;
mod engine_impl;
mod paths;
mod preflight;
#[cfg(windows)]
mod windows_io;

mod engine {
    pub use crate::engine_impl::{format_bps, CopyOpts, DestPhase, JobState};
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::thread::JoinHandle;

    pub fn start_job(
        source: PathBuf,
        dests: Vec<PathBuf>,
        opts: CopyOpts,
    ) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
        let folder_name = source.file_name().ok_or_else(|| {
            "El origen debe ser una carpeta con nombre; no se puede duplicar una raíz completa."
                .to_owned()
        })?;
        let effective_dests = dests
            .into_iter()
            .map(|base| base.join(folder_name))
            .collect();
        crate::preflight::start_job(source, effective_dests, opts)
    }
}

use app::CopierApp;
use eframe::egui;
use std::sync::Arc;

fn app_icon() -> Option<Arc<egui::IconData>> {
    let bytes = include_bytes!(concat!(env!("OUT_DIR"), "/RepartoCopier-runtime.png"));
    eframe::icon_data::from_png_bytes(bytes).ok().map(Arc::new)
}

fn main() -> eframe::Result<()> {
    let mut viewport = egui::ViewportBuilder::default()
        .with_inner_size([960.0, 620.0])
        .with_min_inner_size([680.0, 460.0])
        .with_clamp_size_to_monitor_size(true)
        .with_title("RepartoCopier");
    if let Some(icon) = app_icon() {
        viewport = viewport.with_icon(icon);
    }
    let options = eframe::NativeOptions {
        viewport,
        centered: true,
        ..Default::default()
    };
    eframe::run_native(
        "RepartoCopier",
        options,
        Box::new(|_cc| Ok(Box::new(CopierApp::new()))),
    )
}
