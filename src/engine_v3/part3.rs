fn deliver_to_active(
    active: &mut Vec<usize>,
    senders: &[mpsc::Sender<FanoutItem>],
    controls: &[Arc<DestControl>],
    state: &JobState,
    counts_data: bool,
    make_item: impl Fn() -> FanoutItem,
) {
    let slots = active.clone();
    for slot in slots {
        if !controls[slot].alive.load(Ordering::Acquire) {
            active.retain(|&s| s != slot);
            continue;
        }

        let mut item = make_item();
        let mut depth_reserved = false;
        if counts_data {
            controls[slot].queue_depth.fetch_add(1, Ordering::AcqRel);
            depth_reserved = true;
        }

        loop {
            if !wait_pause(state) {
                if depth_reserved {
                    controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel);
                }
                active.clear();
                return;
            }
            match senders[slot].send_timeout(item, SEND_POLL) {
                Ok(()) => {
                    if counts_data {
                        state.dests.lock().unwrap()[slot].queue_depth =
                            controls[slot].queue_depth.load(Ordering::Acquire);
                    }
                    break;
                }
                Err(mpsc::SendTimeoutError::Timeout(returned)) => {
                    item = returned;
                }
                Err(mpsc::SendTimeoutError::Disconnected(_)) => {
                    if depth_reserved {
                        controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel);
                    }
                    controls[slot].alive.store(false, Ordering::Release);
                    active.retain(|&s| s != slot);
                    break;
                }
            }
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
        let mut state_cache: Vec<HashSet<String>> = dests.iter().map(|d| load_state(d)).collect();

        for info in files.iter() {
            if !wait_pause(&state) { break; }
            let key = state_key(info);
            let mut skip_mask = vec![false; dests.len()];

            for slot in 0..dests.len() {
                if !controls[slot].alive.load(Ordering::Acquire) { continue; }
                let src = source.join(&info.rel);
                let dst = dests[slot].join(&info.rel);
                let physically_valid = same_enough(&src, &dst);
                let state_valid = state_cache[slot].contains(&key) && physically_valid;
                skip_mask[slot] = state_valid || (opts.skip_same && physically_valid);
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
            );
            if active.is_empty() { continue; }

            let path = source.join(&info.rel);
            if let Err(e) = validate_source_snapshot(&path, info) {
                for &slot in &active { set_error(&state, slot, e.clone()); }
                state.cancel.store(true, Ordering::Release);
                break;
            }

            let mut input = match File::open(&path) {
                Ok(f) => f,
                Err(e) => {
                    let msg = format!("origen {}: {e}", path.display());
                    for &slot in &active { set_error(&state, slot, msg.clone()); }
                    state.cancel.store(true, Ordering::Release);
                    break;
                }
            };

            let mut hasher = blake3::Hasher::new();
            let mut copied = 0u64;
            let mut read_ok = true;

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
                        state.cancel.store(true, Ordering::Release);
                        read_ok = false;
                        break;
                    }
                };
                raw.truncate(n);
                hasher.update(&raw);
                copied += n as u64;
                let buf = Arc::new(Buffer { data: raw, pool: Arc::clone(&pool) });
                deliver_to_active(
                    &mut active,
                    &senders,
                    &controls,
                    &state,
                    true,
                    || FanoutItem::Data(Arc::clone(&buf)),
                );
                if active.is_empty() { break; }
            }

            if !read_ok || active.is_empty() {
                if state.cancel.load(Ordering::Relaxed) { break; }
                continue;
            }

            if copied != info.size {
                let msg = format!("origen cambió: {}", path.display());
                for &slot in &active { set_error(&state, slot, msg.clone()); }
                state.cancel.store(true, Ordering::Release);
                break;
            }

            if let Err(e) = validate_source_snapshot(&path, info) {
                for &slot in &active { set_error(&state, slot, e.clone()); }
                state.cancel.store(true, Ordering::Release);
                break;
            }

            let hash = *hasher.finalize().as_bytes();
            deliver_to_active(
                &mut active,
                &senders,
                &controls,
                &state,
                false,
                || FanoutItem::End { hash },
            );

            // Cache is only an optimization for this run. Workers persist state only after commit.
            for &slot in &active {
                state_cache[slot].insert(key.clone());
            }
        }

        drop(senders);
    });

    handles.push(reader);
    handles
}
