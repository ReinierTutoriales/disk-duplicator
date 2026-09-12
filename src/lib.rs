pub mod config;
pub mod copy_plan;
mod engine_impl;
pub mod paths;
pub mod preflight;
pub mod session;
pub mod storage;
#[cfg(windows)]
mod windows_io;

pub mod engine {
    pub use crate::engine_impl::{format_bps, CopyOpts, DestPhase, JobState};
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::thread::JoinHandle;

    pub fn start_job(
        source: PathBuf,
        dests: Vec<PathBuf>,
        opts: CopyOpts,
    ) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
        let meta = std::fs::symlink_metadata(&source).map_err(|e| {
            format!(
                "No se pudo inspeccionar el origen {}: {e}",
                source.display()
            )
        })?;
        let source_name = source.file_name().ok_or_else(|| {
            "El origen debe tener un nombre; no se puede duplicar una raíz completa.".to_owned()
        })?;
        let effective_dests = if meta.is_dir() {
            dests
                .into_iter()
                .map(|base| base.join(source_name))
                .collect()
        } else if meta.is_file() {
            dests
        } else {
            return Err("El origen debe ser un archivo regular o una carpeta.".to_owned());
        };
        crate::preflight::start_job(source, effective_dests, opts)
    }
}
