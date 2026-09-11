const MAX_PENDING_PER_DEST: usize = 32;
const SKIP_VERIFY_BUFFER: usize = 4 * 1024 * 1024;

type PendingQueues = Vec<std::collections::VecDeque<FanoutItem>>;

fn item_is_data(item: &FanoutItem) -> bool {
    matches!(item, FanoutItem::Data(_))
}

fn drop_pending_slot(
    slot: usize,
    pending: &mut PendingQueues,
    controls: &[Arc<DestControl>],
    state: &JobState,
) {
    while let Some(item) = pending[slot].pop_front() {
        if item_is_data(&item) {
            controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel);
        }
    }
    state.dests.lock().unwrap()[slot].queue_depth =
        controls[slot].queue_depth.load(Ordering::Acquire);
}

fn fail_stalled_destination(
    slot: usize,
    pending: &mut PendingQueues,
    controls: &[Arc<DestControl>],
    state: &JobState,
) {
    if !controls[slot].alive.swap(false, Ordering::AcqRel) {
        return;
    }
    drop_pending_slot(slot, pending, controls, state);
    let limit = controls[slot].stall_limit_secs();
    let mut dests = state.dests.lock().unwrap();
    let dp = &mut dests[slot];
    dp.phase = DestPhase::Failed;
    dp.files_err = dp.files_err.saturating_add(1);
    dp.error = Some(format!(
        "Destino atascado: sin progreso durante {limit} s en la operación actual."
    ));
}

fn flush_pending_once(
    slot: usize,
    senders: &[mpsc::Sender<FanoutItem>],
    controls: &[Arc<DestControl>],
    state: &JobState,
    pending: &mut PendingQueues,
) {
    if !controls[slot].alive.load(Ordering::Acquire) {
        drop_pending_slot(slot, pending, controls, state);
        return;
    }

    loop {
        let Some(item) = pending[slot].pop_front() else { break; };
        match senders[slot].try_send(item) {
            Ok(()) => {}
            Err(mpsc::TrySendError::Full(returned)) => {
                pending[slot].push_front(returned);
                break;
            }
            Err(mpsc::TrySendError::Disconnected(returned)) => {
                if item_is_data(&returned) {
                    controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel);
                }
                controls[slot].alive.store(false, Ordering::Release);
                drop_pending_slot(slot, pending, controls, state);
                break;
            }
        }
    }
}

fn make_pending_room(
    slot: usize,
    senders: &[mpsc::Sender<FanoutItem>],
    controls: &[Arc<DestControl>],
    state: &JobState,
    pending: &mut PendingQueues,
) -> bool {
    let mut seen_progress = controls[slot].progress_seq();
    let mut last_progress = Instant::now();

    while pending[slot].len() >= MAX_PENDING_PER_DEST {
        if !wait_pause(state) { return false; }

        flush_pending_once(slot, senders, controls, state, pending);
        if !controls[slot].alive.load(Ordering::Acquire) { return false; }

        let current_progress = controls[slot].progress_seq();
        if current_progress != seen_progress {
            seen_progress = current_progress;
            last_progress = Instant::now();
        }

        if pending[slot].len() >= MAX_PENDING_PER_DEST
            && controls[slot].stall_timed_out(last_progress)
        {
            fail_stalled_destination(slot, pending, controls, state);
            return false;
        }

        if pending[slot].len() >= MAX_PENDING_PER_DEST {
            thread::sleep(Duration::from_millis(1));
        }
    }
    true
}

fn deliver_to_active(
    active: &mut Vec<usize>,
    senders: &[mpsc::Sender<FanoutItem>],
    controls: &[Arc<DestControl>],
    state: &JobState,
    counts_data: bool,
    make_item: impl Fn() -> FanoutItem,
    pending: &mut PendingQueues,
) {
    let slots = active.clone();

    for &slot in &slots {
        flush_pending_once(slot, senders, controls, state, pending);
    }

    for slot in slots {
        if !controls[slot].alive.load(Ordering::Acquire) {
            active.retain(|&s| s != slot);
            continue;
        }

        if !make_pending_room(slot, senders, controls, state, pending) {
            if !controls[slot].alive.load(Ordering::Acquire) {
                active.retain(|&s| s != slot);
            }
            continue;
        }

        let item = make_item();
        if counts_data {
            controls[slot].queue_depth.fetch_add(1, Ordering::AcqRel);
        }

        if !pending[slot].is_empty() {
            pending[slot].push_back(item);
        } else {
            match senders[slot].try_send(item) {
                Ok(()) => {}
                Err(mpsc::TrySendError::Full(returned)) => {
                    pending[slot].push_back(returned);
                }
                Err(mpsc::TrySendError::Disconnected(returned)) => {
                    if item_is_data(&returned) {
                        controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel);
                    }
                    controls[slot].alive.store(false, Ordering::Release);
                    active.retain(|&s| s != slot);
                }
            }
        }

        if counts_data {
            state.dests.lock().unwrap()[slot].queue_depth =
                controls[slot].queue_depth.load(Ordering::Acquire);
        }
    }
}

fn drain_pending(
    active: &mut Vec<usize>,
    senders: &[mpsc::Sender<FanoutItem>],
    controls: &[Arc<DestControl>],
    state: &JobState,
    pending: &mut PendingQueues,
) {
    let mut seen_progress: Vec<u64> = controls.iter().map(|c| c.progress_seq()).collect();
    let mut last_progress: Vec<Instant> = (0..pending.len()).map(|_| Instant::now()).collect();

    loop {
        if !wait_pause(state) { break; }
        let mut any = false;

        for slot in 0..pending.len() {
            if pending[slot].is_empty() { continue; }
            any = true;
            flush_pending_once(slot, senders, controls, state, pending);

            if !controls[slot].alive.load(Ordering::Acquire) { continue; }
            let current = controls[slot].progress_seq();
            if current != seen_progress[slot] {
                seen_progress[slot] = current;
                last_progress[slot] = Instant::now();
            }

            if !pending[slot].is_empty() && controls[slot].stall_timed_out(last_progress[slot]) {
                fail_stalled_destination(slot, pending, controls, state);
            }
        }

        active.retain(|&slot| controls[slot].alive.load(Ordering::Acquire));
        if !any || pending.iter().all(|q| q.is_empty()) { break; }
        thread::sleep(Duration::from_millis(1));
    }

    if state.cancel.load(Ordering::Relaxed) {
        for slot in 0..pending.len() {
            drop_pending_slot(slot, pending, controls, state);
        }
    }
}

fn mark_skipped_all(state: &JobState, info: &FileInfo, mask: &[bool]) {
    let mut g = state.dests.lock().unwrap();
    for (slot, skip) in mask.iter().enumerate() {
        if *skip {
            g[slot].files_skip += 1;
            g[slot].files_done += 1;
            g[slot].written += info.size;
        }
    }
}

fn fanout_job(
    source: PathBuf,
    dests: Vec<PathBuf>,
    files: Arc<Vec<FileInfo>>,
    state: Arc<JobState>,
    opts: CopyOpts,
    preflight_verified_skips: Option<Arc<Vec<HashSet<PathBuf>>>>,
) -> Vec<JoinHandle<()>> {
    let q = queue_depth_for(dests.len());
    let max_buffers = (RESERVED_RAM / BLOCK).max(8);
    let pool = BufferPool::new(max_buffers, Arc::clone(&state.buffers_in_flight));
    let mut senders = Vec::with_capacity(dests.len());
    let mut controls = Vec::with_capacity(dests.len());
    let mut handles = Vec::with_capacity(dests.len() + 1);

    for (slot, dest) in dests.iter().cloned().enumerate() {
        let (tx, rx) = mpsc::bounded(q);
        let control = Arc::new(DestControl::new());
        controls.push(Arc::clone(&control));
        let st = Arc::clone(&state);
        handles.push(thread::spawn(move || fanout_worker(dest, rx, control, st, slot, opts)));
        senders.push(tx);
    }

    let reader = thread::spawn(move || {
        let state_cache: Vec<HashSet<String>> = dests.iter().map(|d| load_state(d)).collect();
        let mut pending: PendingQueues = (0..dests.len())
            .map(|_| std::collections::VecDeque::new())
            .collect();
        let mut all_active: Vec<usize> = (0..dests.len()).collect();
        let mut skip_verify_buf = vec![0u8; SKIP_VERIFY_BUFFER];

        'files: for info in files.iter() {
            if !wait_pause(&state) { break; }
            let key = fast_state_key(info);
            let src = source.join(&info.rel);
            let mut skip_mask = vec![false; dests.len()];
            let mut source_skip_hash: Option<[u8; 32]> = None;

            for slot in 0..dests.len() {
                if !controls[slot].alive.load(Ordering::Acquire) { continue; }
                let dst = dests[slot].join(&info.rel);
                let physically_valid = same_enough(&src, &dst);
                let state_valid = state_cache[slot].contains(&key) && physically_valid;
                let preflight_valid = preflight_verified_skips
                    .as_ref()
                    .and_then(|sets| sets.get(slot))
                    .is_some_and(|set| set.contains(&info.rel));

                if state_valid || (preflight_valid && physically_valid) {
                    skip_mask[slot] = true;
                    continue;
                }

                if opts.skip_same && physically_valid {
                    let expected = match source_skip_hash {
                        Some(hash) => hash,
                        None => match hash_file_with_buffer(&src, &mut skip_verify_buf, Some(&state)) {
                            Ok(hash) => {
                                let bytes = *hash.as_bytes();
                                state.reader_hashes.lock().unwrap().insert(info.rel.clone(), bytes);
                                source_skip_hash = Some(bytes);
                                bytes
                            }
                            Err(e) => {
                                for &active_slot in &all_active {
                                    set_error(&state, active_slot, e.clone());
                                }
                                state.request_cancel();
                                break 'files;
                            }
                        },
                    };

                    if let Ok(actual) = hash_file_with_buffer(&dst, &mut skip_verify_buf, Some(&state)) {
                        skip_mask[slot] = actual.as_bytes() == &expected;
                    }
                }
            }

            mark_skipped_all(&state, info, &skip_mask);
            let mut active: Vec<usize> = (0..dests.len())
                .filter(|&slot| !skip_mask[slot] && controls[slot].alive.load(Ordering::Acquire))
                .collect();

            deliver_to_active(
                &mut active,
                &senders,
                &controls,
                &state,
                false,
                || FanoutItem::Begin(info.clone()),
                &mut pending,
            );
            if active.is_empty() { continue; }

            let path = source.join(&info.rel);
            if let Err(e) = validate_source_snapshot(&path, info) {
                for &slot in &active { set_error(&state, slot, e.clone()); }
                state.request_cancel();
                break;
            }

            let mut hasher = blake3::Hasher::new();
            let mut copied = 0u64;
            let mut read_ok = true;

            #[cfg(windows)]
            {
                let mut input = match crate::windows_io::CancelableReader::open(&path) {
                    Ok(reader) => reader,
                    Err(e) => {
                        let msg = format!("origen {}: {e}", path.display());
                        for &slot in &active { set_error(&state, slot, msg.clone()); }
                        state.request_cancel();
                        break 'files;
                    }
                };

                let Some(first_raw) = pool.acquire(&state) else {
                    if state.cancel.load(Ordering::Relaxed) { break 'files; }
                    continue;
                };
                let mut pending_read = input.start_read(first_raw);

                loop {
                    if !wait_pause(&state) {
                        let raw = input.cancel_read(pending_read);
                        pool.release(raw);
                        read_ok = false;
                        break;
                    }

                    let (raw, read_result) = input.finish_read(pending_read, || {
                        state.cancel.load(Ordering::Acquire)
                    });
                    let n = match read_result {
                        Ok(0) => {
                            pool.release(raw);
                            break;
                        }
                        Ok(n) => n,
                        Err(e) => {
                            pool.release(raw);
                            let msg = format!("lectura {}: {e}", path.display());
                            for &slot in &active { set_error(&state, slot, msg.clone()); }
                            if e.kind() != std::io::ErrorKind::Interrupted {
                                state.request_cancel();
                            }
                            read_ok = false;
                            break;
                        }
                    };

                    let mut next_pending = pool
                        .try_acquire(&state)
                        .map(|next_raw| input.start_read(next_raw));

                    hasher.update(&raw[..n]);
                    copied += n as u64;
                    let buf = Arc::new(Buffer {
                        data: raw,
                        len: n,
                        pool: Arc::clone(&pool),
                    });
                    deliver_to_active(
                        &mut active,
                        &senders,
                        &controls,
                        &state,
                        true,
                        || FanoutItem::Data(Arc::clone(&buf)),
                        &mut pending,
                    );

                    if active.is_empty() {
                        if let Some(next) = next_pending.take() {
                            let raw = input.cancel_read(next);
                            pool.release(raw);
                        }
                        break;
                    }

                    pending_read = if let Some(next) = next_pending {
                        next
                    } else {
                        let Some(next_raw) = pool.acquire(&state) else {
                            read_ok = false;
                            break;
                        };
                        input.start_read(next_raw)
                    };
                }
            }

            #[cfg(not(windows))]
            {
                let mut input = match File::open(&path) {
                    Ok(f) => f,
                    Err(e) => {
                        let msg = format!("origen {}: {e}", path.display());
                        for &slot in &active { set_error(&state, slot, msg.clone()); }
                        state.request_cancel();
                        break 'files;
                    }
                };

                loop {
                    if !wait_pause(&state) {
                        read_ok = false;
                        break;
                    }
                    let Some(mut raw) = pool.acquire(&state) else {
                        read_ok = false;
                        break;
                    };
                    let n = match input.read(&mut raw) {
                        Ok(0) => {
                            pool.release(raw);
                            break;
                        }
                        Ok(n) => n,
                        Err(e) => {
                            pool.release(raw);
                            let msg = format!("lectura {}: {e}", path.display());
                            for &slot in &active { set_error(&state, slot, msg.clone()); }
                            state.request_cancel();
                            read_ok = false;
                            break;
                        }
                    };
                    hasher.update(&raw[..n]);
                    copied += n as u64;
                    let buf = Arc::new(Buffer {
                        data: raw,
                        len: n,
                        pool: Arc::clone(&pool),
                    });
                    deliver_to_active(
                        &mut active,
                        &senders,
                        &controls,
                        &state,
                        true,
                        || FanoutItem::Data(Arc::clone(&buf)),
                        &mut pending,
                    );
                    if active.is_empty() { break; }
                }
            }

            if !read_ok || active.is_empty() {
                if state.cancel.load(Ordering::Relaxed) { break; }
                continue;
            }

            if copied != info.size {
                let msg = format!("origen cambió: {}", path.display());
                for &slot in &active { set_error(&state, slot, msg.clone()); }
                state.request_cancel();
                break;
            }

            if let Err(e) = validate_source_snapshot(&path, info) {
                for &slot in &active { set_error(&state, slot, e.clone()); }
                state.request_cancel();
                break;
            }

            let hash = *hasher.finalize().as_bytes();
            state.reader_hashes.lock().unwrap().insert(info.rel.clone(), hash);
            deliver_to_active(
                &mut active,
                &senders,
                &controls,
                &state,
                false,
                || FanoutItem::End { hash },
                &mut pending,
            );
        }

        drain_pending(
            &mut all_active,
            &senders,
            &controls,
            &state,
            &mut pending,
        );
        drop(senders);
    });

    handles.push(reader);
    handles
}