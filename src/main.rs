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
use std::ffi::OsString;
use std::path::PathBuf;
use std::sync::Arc;

fn app_icon() -> Option<Arc<egui::IconData>> {
    let bytes = include_bytes!(concat!(env!("OUT_DIR"), "/RepartoCopier-runtime.png"));
    eframe::icon_data::from_png_bytes(bytes).ok().map(Arc::new)
}

fn launch_source_from_args<I>(args: I) -> Option<PathBuf>
where
    I: IntoIterator<Item = OsString>,
{
    let mut args = args.into_iter();
    let _exe = args.next();
    let first = args.next()?;

    if first == "--source" {
        let source = args.next()?;
        return args.next().is_none().then(|| PathBuf::from(source));
    }

    if first.to_string_lossy().starts_with('-') {
        return None;
    }

    args.next().is_none().then(|| PathBuf::from(first))
}

fn main() -> eframe::Result<()> {
    let launch_source = launch_source_from_args(std::env::args_os());
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
        Box::new(move |_cc| Ok(Box::new(CopierApp::new_with_source(launch_source)))),
    )
}

#[cfg(test)]
mod tests {
    use super::launch_source_from_args;
    use std::ffi::OsString;
    use std::path::PathBuf;

    fn args(values: &[&str]) -> Vec<OsString> {
        values.iter().map(OsString::from).collect()
    }

    #[test]
    fn launch_source_accepts_explicit_and_positional_paths() {
        assert_eq!(
            launch_source_from_args(args(&["RepartoCopier.exe", "--source", r"C:\Música\Niño"])),
            Some(PathBuf::from(r"C:\Música\Niño"))
        );
        assert_eq!(
            launch_source_from_args(args(&["RepartoCopier.exe", r"\servidor\Datos compartidos"])),
            Some(PathBuf::from(r"\servidor\Datos compartidos"))
        );
    }

    #[test]
    fn launch_source_rejects_ambiguous_or_unknown_arguments() {
        assert_eq!(launch_source_from_args(args(&["RepartoCopier.exe"])), None);
        assert_eq!(
            launch_source_from_args(args(&["RepartoCopier.exe", "--unknown", r"C:\Origen"])),
            None
        );
        assert_eq!(
            launch_source_from_args(args(&["RepartoCopier.exe", r"C:\Uno", r"D:\Dos"])),
            None
        );
    }
}
