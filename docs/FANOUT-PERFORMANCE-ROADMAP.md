# FAN-OUT Performance Audit & Roadmap

## Objective

Reach or exceed ExtremeCopy-style FAN-OUT behavior on independent physical destinations while preserving Disk Duplicator correctness guarantees: one source read, exact directory replication, empty directories, no mirror-delete, bounded memory, cancellation, recovery, integrity, and topology-aware scheduling.

Performance is considered proven only by repeatable hardware benchmarks. Architectural similarity alone is not a throughput claim.

## Confirmed architecture today

- One source read path per file.
- One `SharedBlock` payload is reference-counted across all active destinations.
- Per-destination workers and channels.
- Explicit-offset asynchronous writes.
- Physical-device-aware schedulers; partitions on the same physical disk share one scheduler when identity is exact.
- Global adaptive byte budget bounds live FAN-OUT payload memory.
- Source prefetch + BLAKE3 hash pipeline for large files.
- Telemetry already separates source read, hash, write, queue/fan-out waits, buffer waits, flush/commit/recovery, and physical-device backlog/QD.

## ExtremeCopy findings that matter

ExtremeCopy builds a source -> duplicate-output -> per-destination transfer graph. The duplicate stage broadcasts the same source data to independent downstream branches. Different physical destination storage uses asynchronous transfer filters; same-physical-device cases are handled more conservatively. Some source paths use `FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN`.

The important lesson is not to copy the legacy implementation literally. The useful principles are:

1. Read the source once.
2. Broadcast shared data to independent target pipelines.
3. Keep target branches decoupled long enough to absorb transient latency.
4. Use physical topology to avoid destructive concurrency on one device.
5. Do not impose a per-block global barrier unless bounded-memory pressure actually requires it.

## Changes already implemented

### Phase 1 — topology-aware branch buffering

Initial performance change:

| Branch | Previous | Phase 1 |
|---|---:|---:|
| Network | 32 MiB | 32 MiB |
| USB flash | 16 MiB | 64 MiB |
| Rotational HDD | 32 MiB | 64 MiB |
| Uncertain USB SSD | 32 MiB / QD1 | 64 MiB / QD1 |
| Exact USB SSD | 64 MiB / QD1 if removable | 128 MiB / QD2 |
| SATA SSD | 64 MiB / QD2 | 128 MiB / QD2 |
| NVMe | 128 MiB / QD2 | 256 MiB / QD2 |

This phase passed the full Windows .NET CI after aligning the USB SSD policy tests.

### Phase 2 — aggressive but bounded run-ahead

The 16 MiB large-file block is shared by reference across destinations, so increasing branch backlog does not create one payload copy per destination. The global `AdaptiveByteBudget` remains the hard payload-memory ceiling.

New targets:

| Branch | Phase 2 target | 16 MiB blocks | QD |
|---|---:|---:|---:|
| Network | 32 MiB | 2 | 1 |
| Unknown/local conservative | 64 MiB | 4 | 1 |
| USB flash | 128 MiB | 8 | 1 |
| Rotational HDD | 128 MiB | 8 | 1 |
| Uncertain USB SSD | 128 MiB | 8 | 1 |
| Exact USB SSD/UASP | 256 MiB | 16 | 2 |
| SATA SSD | 256 MiB | 16 | 2 |
| NVMe | 512 MiB | 32 | 2 |

QD is deliberately not raised beyond 2 yet. Queue depth and run-ahead solve different problems: backlog absorbs pipeline jitter; QD controls simultaneous physical writes. Raising QD without device-specific evidence can reduce performance or increase latency.

## Current bottlenecks / audit findings

### P0 — deferred-branch head-of-line blocking

`DeliverDataAsync` first gives a block to branches that can reserve backlog immediately. Saturated branches are placed in a deferred list. The deferred list is then awaited sequentially.

Consequence: if deferred branch A is still full while deferred branch B becomes ready, B cannot receive the current block until A completes its reservation. This is unnecessary cross-device head-of-line blocking and is less independent than the ExtremeCopy transfer graph.

Planned correction:
- reserve/wake deferred branches independently;
- deliver the current shared block to each branch as soon as its own credit becomes available;
- retain ordering inside each destination;
- retain one reference per destination;
- retain global payload-memory bound;
- preserve cancellation/failure accounting.

Required tests:
- two independently throttled deferred branches released in reverse order;
- verify the earlier-ready branch receives data first;
- cancellation while one reservation is pending does not leak backlog or `SharedBlock` references;
- failed destination does not stall healthy branches.

### P0 — real hardware benchmark harness

Architecture cannot prove 150 MB/s per destination. Add/export a repeatable benchmark result containing:

- source physical read MB/s;
- per-destination write MB/s;
- aggregate logical write MB/s;
- wall time;
- source/hash/write CPU time;
- `FanoutWait` / queue wait / buffer wait;
- per-device backlog target, current and peak bytes;
- max/peak outstanding I/O;
- peak live FAN-OUT memory;
- file count / total bytes / workload class;
- source and destination physical-device identities and bus/media types.

Acceptance target for independent capable devices: adding destinations must not divide source throughput by N. Example: a ~150 MB/s source should allow each sufficiently fast independent destination to approach ~150 MB/s, subject to controller/bus/device limits.

### P1 — same-physical source/destination scheduling

Current scheduler forces destination QD1 when source and destination share a physical device, but source prefetch is not fully coordinated with destination writes. On an HDD this can still create read/write seek oscillation.

Planned correction:
- detect same-physical source/destination early;
- use a conservative shared-device pipeline policy;
- reduce/disable deep source prefetch when it would induce HDD seek thrash;
- benchmark HDD same-device copy separately from independent-disk FAN-OUT.

### P1 — adaptive branch backlog

Static targets are safe starting points, not final optimization.

Candidate policy:
- increase branch window when writer starvation is observed and memory headroom exists;
- hold/reduce when branch queue remains saturated;
- never exceed global adaptive memory budget;
- preserve a lower floor for independent physical disks to avoid transient jitter collapsing the whole FAN-OUT.

### P1 — source prefetch depth

Large-file physical prefetch currently has a small bounded capacity relative to the new SSD/NVMe branch windows. Benchmark increasing/adapting prefetch after P0 head-of-line removal. Do not simply maximize it; source HDD and memory pressure must remain stable.

### P1 — direct/unbuffered source fast path

ExtremeCopy uses `FILE_FLAG_NO_BUFFERING` in relevant source paths. Disk Duplicator currently uses asynchronous buffered sequential I/O.

Do not enable unbuffered I/O globally. A correct implementation needs:
- sector/alignment discovery;
- aligned native buffers;
- aligned offsets and lengths;
- tail handling;
- buffered fallback;
- benchmark comparison on HDD, USB SSD, SATA SSD and NVMe.

Only keep the fast path where measurements show a win.

### P1 — per-file durable flush cost

Large files currently call durable flush before commit. This protects correctness but can dominate workloads with many files.

Audit options without weakening recovery semantics:
- retain write-through for tiny files;
- measure flush time separately;
- consider batched/checkpoint durability only if crash-recovery invariants remain explicit and tested.

### P2 — hashing cost at multi-GB/s

BLAKE3 during source read is unlikely to explain a 150 -> 75 MB/s collapse, but it can become material on NVMe-class throughput. Keep integrity by default; benchmark hash CPU utilization and only optimize if it becomes a measured bottleneck.

## Test matrix

Run all cases with 1, 2 and 4 independent destinations where hardware permits:

- source: USB flash, SATA HDD, SATA SSD, NVMe;
- destinations: homogeneous fast targets, mixed-speed targets, one intentionally slow target;
- workload: one very large file, medium files, many small files, empty directories;
- verify disabled/enabled;
- skip-same disabled/enabled where applicable;
- same physical source/destination special case;
- cancellation during source read, backlog wait and physical write;
- destination failure while other targets remain healthy.

Correctness gates:
- selected root folder name preserved;
- exact directory structure preserved;
- empty directories preserved;
- no `.disk-duplicator` state inside copied tree;
- no mirror-delete behavior;
- hashes/sizes correct;
- no leaked `.part`/backup files after successful completion;
- recovery remains valid after interruption.

## Performance decision rules

Do not accept an optimization because it "looks faster" in code.

Keep a change only when:
1. CI and correctness tests pass;
2. it does not weaken topology safety, recovery or bounded-memory guarantees;
3. benchmark median improves or removes a demonstrated stall;
4. regressions on other workload classes are understood and acceptable;
5. the result is reproducible.

For the ExtremeCopy comparison, the minimum success criterion is parity on the same source/targets/workload. The project goal is to exceed it where modern async I/O, topology detection and adaptive scheduling provide a measurable advantage.
