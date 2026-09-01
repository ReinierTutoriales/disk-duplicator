#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod disks;
mod engine;
mod winpath;

use app::DuplicatorApp;
use eframe::egui;

fn main() -> eframe::Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1100.0, 640.0])
            .with_min_inner_size([900.0, 520.0])
            .with_title("Disk Duplicator"),
        centered: true,
        ..Default::default()
    };
    eframe::run_native(
        "Disk Duplicator",
        options,
        Box::new(|_cc| Ok(Box::new(DuplicatorApp::new()))),
    )
}
