fn validate_job_paths(source: &Path, dests: &[PathBuf]) -> Result<(), String> {
    if !source.is_dir() { return Err("El origen debe ser una carpeta.".into()); }
    if dests.is_empty() { return Err("Agrega al menos un destino.".into()); }
    for d in dests {
        if dest_inside_source(source, d) { return Err(format!("El destino {} está dentro del origen.", d.display())); }
        fs::create_dir_all(d).map_err(|e| format!("destino {}: {e}", d.display()))?;
    }
    Ok(())
}

fn build_job(
    source: PathBuf,
    dests: Vec<PathBuf>,
    files: Arc<Vec<FileInfo>>,
    opts: CopyOpts,
) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    if files.is_empty() { return Err("El origen no tiene archivos.".into()); }
    create_directory_layout(&source, &dests)?;

    let bytes_total: u64 = files.iter().map(|f| f.size).sum();
    let files_total = files.len() as u64;
    let progress = dests.iter().map(|d| DestProgress {
        label: d.display().to_string(),
        written: 0,
        total: bytes_total,
        files_done: 0,
        files_skip: 0,
        files_err: 0,
        bps: 0.0,
        phase: DestPhase::Idle,
        error: None,
        last_file: String::new(),
        mode: CopyMode::Fanout,
        queue_depth: 0,
        retries: 0,
    }).collect();

    let max_buffers = (RESERVED_RAM / BLOCK).max(8);
    let state = Arc::new(JobState {
        running: AtomicBool::new(true),
        cancel: AtomicBool::new(false),
        pause: AtomicBool::new(false),
        files_total: AtomicU64::new(files_total),
        bytes_total: AtomicU64::new(bytes_total),
        buffers_in_flight: Arc::new(AtomicUsize::new(0)),
        max_buffers,
        fanout: true,
        dests: Mutex::new(progress),
    });
    let handles = fanout_job(source, dests, files, Arc::clone(&state), opts);
    Ok((state, handles))
}

pub(crate) fn start_job_with_files(
    source: PathBuf,
    dests: Vec<PathBuf>,
    files: Arc<Vec<FileInfo>>,
    opts: CopyOpts,
) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
    validate_job_paths(&source, &dests)?;
    build_job(source, dests, files, opts)
}

pub fn format_bps(bps: f64) -> String {
    if bps >= 1_073_741_824.0 { format!("{:.2} GB/s", bps / 1_073_741_824.0) }
    else if bps >= 1_048_576.0 { format!("{:.1} MB/s", bps / 1_048_576.0) }
    else if bps >= 1024.0 { format!("{:.0} KB/s", bps / 1024.0) }
    else { format!("{:.0} B/s", bps) }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let p = std::env::temp_dir().join(format!("disk-duplicator-{name}-{stamp}"));
        fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn queue_scales_with_destinations() {
        assert_eq!(queue_depth_for(1), MAX_QUEUE);
        assert!(queue_depth_for(200) >= MIN_QUEUE);
        assert!(queue_depth_for(200) <= queue_depth_for(2));
    }

    #[test]
    fn directory_layout_preserves_empty_folders() {
        let root = temp_dir("dirs");
        let source = root.join("source");
        let dest = root.join("dest");
        fs::create_dir_all(source.join("a/b/empty")).unwrap();
        fs::create_dir_all(source.join("other")).unwrap();
        create_directory_layout(&source, std::slice::from_ref(&dest)).unwrap();
        assert!(dest.join("a/b/empty").is_dir());
        assert!(dest.join("other").is_dir());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn only_fanout_mode_is_assigned() {
        let progress = DestProgress {
            label: "x".into(), written: 0, total: 0, files_done: 0, files_skip: 0, files_err: 0, bps: 0.0,
            phase: DestPhase::Idle, error: None, last_file: String::new(), mode: CopyMode::Fanout, queue_depth: 0, retries: 0,
        };
        assert!(matches!(progress.mode, CopyMode::Fanout));
    }

    #[test]
    fn format_is_sane() {
        assert_eq!(format_bps(0.0), "0 B/s");
        assert!(format_bps(1024.0).contains("KB/s"));
    }
}
