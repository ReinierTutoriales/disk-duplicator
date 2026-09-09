fn deliver_to_active(
    active: &mut Vec<usize>,
    senders: &[mpsc::Sender<FanoutItem>],
    controls: &[Arc<DestControl>],
    state: &JobState,
    counts_data: bool,
    make_item: impl Fn() -> FanoutItem,
) {
    let mut pending: Vec<(usize, FanoutItem)> = Vec::with_capacity(active.len());
    for &slot in active.iter() {
        if !controls[slot].alive.load(Ordering::Acquire) { continue; }
        if counts_data { controls[slot].queue_depth.fetch_add(1, Ordering::AcqRel); }
        match senders[slot].try_send(make_item()) {
            Ok(()) => {
                if counts_data { state.dests.lock().unwrap()[slot].queue_depth = controls[slot].queue_depth.load(Ordering::Acquire); }
            }
            Err(mpsc::TrySendError::Full(item)) => {
                if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                pending.push((slot, item));
            }
            Err(mpsc::TrySendError::Disconnected(_)) => {
                if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                controls[slot].alive.store(false, Ordering::Release);
            }
        }
    }

    while !pending.is_empty() && wait_pause(state) {
        let mut next = Vec::with_capacity(pending.len());
        for (slot, item) in pending {
            if !controls[slot].alive.load(Ordering::Acquire) { continue; }
            if counts_data { controls[slot].queue_depth.fetch_add(1, Ordering::AcqRel); }
            match senders[slot].try_send(item) {
                Ok(()) => {
                    if counts_data { state.dests.lock().unwrap()[slot].queue_depth = controls[slot].queue_depth.load(Ordering::Acquire); }
                }
                Err(mpsc::TrySendError::Full(item)) => {
                    if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                    next.push((slot, item));
                }
                Err(mpsc::TrySendError::Disconnected(_)) => {
                    if counts_data { controls[slot].queue_depth.fetch_sub(1, Ordering::AcqRel); }
                    controls[slot].alive.store(false, Ordering::Release);
                }
            }
        }
        pending = next;
        if !pending.is_empty() { thread::sleep(DELIVERY_RETRY_SLEEP); }
    }
    active.retain(|&slot| controls[slot].alive.load(Ordering::Acquire));
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
    let budget = BufferBudget::new(max_buffers, Arc::clone(&state.buffers_in_flight));
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
                skip_mask[slot] = state_cache[slot].contains(&key) || (opts.skip_same && same_enough(&src, &dst));
            }
            mark_skipped_all(&state, info, &skip_mask);
            let mut active: Vec<usize> = (0..dests.len())
                .filter(|&slot| !skip_mask[slot] && controls[slot].alive.load(Ordering::Acquire))
                .collect();
            deliver_to_active(&mut active, &senders, &controls, &state, false, || FanoutItem::Begin(info.clone()));
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
                if !wait_pause(&state) { read_ok = false; break; }
                if !budget.acquire(&state) { read_ok = false; break; }
                let mut raw = vec![0u8; BLOCK];
                let n = match input.read(&mut raw) {
                    Ok(0) => { budget.release(); break; }
                    Ok(n) => n,
                    Err(e) => {
                        budget.release();
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
                let buf = Arc::new(Buffer { data: raw.into_boxed_slice(), budget: Arc::clone(&budget) });
                deliver_to_active(&mut active, &senders, &controls, &state, true, || FanoutItem::Data(Arc::clone(&buf)));
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
            deliver_to_active(&mut active, &senders, &controls, &state, false, || FanoutItem::End { hash });
            for &slot in &active { state_cache[slot].insert(key.clone()); }
        }
        drop(senders);
    });
    handles.push(reader);
    handles
}
