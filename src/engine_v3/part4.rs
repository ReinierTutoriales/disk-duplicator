fn validate_job_paths(source: &Path, dests: &[PathBuf]) -> Result<(), String> {
    if !source.is_dir() { return Err("El origen debe ser una carpeta.".into()); }
    if dests.is_empty() { return Err("Agrega al menos un destino.".into()); }
    for d in dests {
        if dest_inside_source(source, d) {
            return Err(format!("El destino {} está dentro del origen.", d.display()));
        }
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
    create_directory_layout(&source, &dests)?;

    let bytes_total: u64 = files.iter().map(|f| f.size).sum();
    let files_total = files.len() as u64;
    let progress: Vec<DestProgress> = dests.iter().map(|d| DestProgress {
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
    debug_assert!(progress.iter().all(|d| matches!(d.mode, CopyMode::Fanout)));

    let max_buffers = (RESERVED_RAM / BLOCK).max(8);
    let state = Arc::new(JobState {
        running: AtomicBool::new(true),
        cancel: AtomicBool::new(false),
        pause: AtomicBool::new(false),
        files_total: AtomicU64::new(files_total),
        bytes_total: AtomicU64::new(bytes_total),
        buffers_in_flight: Arc::new(AtomicUsize::new(0)),
        max_buffers,
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
    if bps >= 1_073_741_824.0 { format!("{:.2} GiB/s", bps / 1_073_741_824.0) }
    else if bps >= 1_048_576.0 { format!("{:.1} MiB/s", bps / 1_048_576.0) }
    else if bps >= 1024.0 { format!("{:.0} KiB/s", bps / 1024.0) }
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

    fn worker_state(dest: &Path, size: u64) -> Arc<JobState> {
        Arc::new(JobState {
            running: AtomicBool::new(true),
            cancel: AtomicBool::new(false),
            pause: AtomicBool::new(false),
            files_total: AtomicU64::new(1),
            bytes_total: AtomicU64::new(size),
            buffers_in_flight: Arc::new(AtomicUsize::new(0)),
            max_buffers: 2,
            dests: Mutex::new(vec![DestProgress {
                label: dest.display().to_string(),
                written: 0,
                total: size,
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
            }]),
        })
    }

    fn data_item(state: &Arc<JobState>, data: &[u8]) -> FanoutItem {
        let pool = BufferPool::new(2, Arc::clone(&state.buffers_in_flight));
        let mut raw = pool.acquire(state).unwrap();
        raw[..data.len()].copy_from_slice(data);
        raw.truncate(data.len());
        FanoutItem::Data(Arc::new(Buffer { data: raw, pool }))
    }

    #[test]
    fn directory_layout_preserves_empty_folders() {
        let root = temp_dir("dirs");
        let source = root.join("source");
        let dest = root.join("dest");
        fs::create_dir_all(source.join("a/b/empty")).unwrap();
        create_directory_layout(&source, std::slice::from_ref(&dest)).unwrap();
        assert!(dest.join("a/b/empty").is_dir());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn state_is_outside_destination_tree() {
        let root = temp_dir("state");
        let dest = root.join("CopyName");
        fs::create_dir_all(&dest).unwrap();
        let state = state_dir_for(&dest);
        assert!(!state.starts_with(&dest));
        assert_eq!(state.parent().unwrap().file_name().unwrap(), ".disk-duplicator-state");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn only_fanout_is_assigned() {
        let d = DestProgress {
            label: "x".into(), written: 0, total: 0, files_done: 0, files_skip: 0,
            files_err: 0, bps: 0.0, phase: DestPhase::Idle, error: None,
            last_file: String::new(), mode: CopyMode::Fanout, queue_depth: 0, retries: 0,
        };
        assert!(matches!(d.mode, CopyMode::Fanout));
    }

    #[test]
    fn buffer_pool_reuses_capacity() {
        let gauge = Arc::new(AtomicUsize::new(0));
        let pool = BufferPool::new(2, Arc::clone(&gauge));
        let state = JobState {
            running: AtomicBool::new(true), cancel: AtomicBool::new(false), pause: AtomicBool::new(false),
            files_total: AtomicU64::new(0), bytes_total: AtomicU64::new(0), buffers_in_flight: gauge,
            max_buffers: 2, dests: Mutex::new(Vec::new()),
        };
        let buf = pool.acquire(&state).unwrap();
        assert_eq!(buf.len(), BLOCK);
        pool.release(buf);
        assert_eq!(state.buffers_in_flight.load(Ordering::Relaxed), 0);
    }

    #[test]
    fn failed_worker_does_not_commit_or_record_completion() {
        let root = temp_dir("dead-worker");
        let dest = root.join("dest");
        fs::create_dir_all(&dest).unwrap();
        let data = b"contenido que no debe llegar al destino";
        let state = worker_state(&dest, data.len() as u64);
        let control = Arc::new(DestControl::new());
        let (tx, rx) = mpsc::bounded(4);
        let opts = CopyOpts { verify: false, skip_same: false, keep_going: true };
        let worker = {
            let dest = dest.clone();
            let control = Arc::clone(&control);
            let state = Arc::clone(&state);
            thread::spawn(move || fanout_worker(dest, rx, control, state, 0, opts))
        };

        let info = FileInfo {
            rel: PathBuf::from("test.bin"),
            size: data.len() as u64,
            mtime_ns: 0,
        };
        tx.send(FanoutItem::Begin(info)).unwrap();
        control.queue_depth.store(1, Ordering::Release);
        tx.send(data_item(&state, data)).unwrap();

        let deadline = Instant::now() + Duration::from_secs(2);
        while control.progress_seq() == 0 && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(2));
        }
        assert!(control.progress_seq() > 0, "el worker no alcanzó la escritura de prueba");

        control.alive.store(false, Ordering::Release);
        let hash = *blake3::hash(data).as_bytes();
        tx.send(FanoutItem::End { hash }).unwrap();
        drop(tx);
        worker.join().unwrap();

        assert!(!dest.join("test.bin").exists(), "un worker marcado como muerto hizo commit");
        let journal = state_path(&dest);
        if journal.exists() {
            assert!(fs::read_to_string(&journal).unwrap().trim().is_empty(), "journal contaminado tras fallo");
        }
        let manifest = manifest_path(&dest);
        if manifest.exists() {
            assert!(fs::read_to_string(&manifest).unwrap().trim().is_empty(), "manifest contaminado tras fallo");
        }
        assert_eq!(state.snapshot()[0].phase, DestPhase::Failed);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn healthy_worker_commits_one_file_and_one_completion_record() {
        let root = temp_dir("healthy-worker");
        let dest = root.join("dest");
        fs::create_dir_all(&dest).unwrap();
        let data = b"contenido correcto";
        let state = worker_state(&dest, data.len() as u64);
        let control = Arc::new(DestControl::new());
        let (tx, rx) = mpsc::bounded(4);
        let opts = CopyOpts { verify: false, skip_same: false, keep_going: true };
        let worker = {
            let dest = dest.clone();
            let control = Arc::clone(&control);
            let state = Arc::clone(&state);
            thread::spawn(move || fanout_worker(dest, rx, control, state, 0, opts))
        };

        let info = FileInfo {
            rel: PathBuf::from("test.bin"),
            size: data.len() as u64,
            mtime_ns: 0,
        };
        tx.send(FanoutItem::Begin(info)).unwrap();
        control.queue_depth.store(1, Ordering::Release);
        tx.send(data_item(&state, data)).unwrap();
        tx.send(FanoutItem::End { hash: *blake3::hash(data).as_bytes() }).unwrap();
        drop(tx);
        worker.join().unwrap();

        assert_eq!(fs::read(dest.join("test.bin")).unwrap(), data);
        let journal = fs::read_to_string(state_path(&dest)).unwrap();
        assert_eq!(journal.lines().filter(|line| !line.is_empty()).count(), 1);
        let manifest = fs::read_to_string(manifest_path(&dest)).unwrap();
        assert_eq!(manifest.lines().filter(|line| !line.is_empty()).count(), 1);
        assert_eq!(state.snapshot()[0].phase, DestPhase::Done);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn format_is_sane() {
        assert_eq!(format_bps(0.0), "0 B/s");
        assert!(format_bps(1024.0).contains("KiB/s"));
    }
}
