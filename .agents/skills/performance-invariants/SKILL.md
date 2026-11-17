---
name: performance-invariants
description: Use for performance research and implementation. Preserves integrity, recovery, bounded memory and backpressure; requires official evidence and reproducible measurements.
---

# Performance & Invariants

## Non-negotiable contracts
- Never sacrifice copy invariants, atomicity, recovery (BLAKE3), fail-closed behavior, cancellation safety, or existing-file policy for speed.
- Preserve the current distinction between shared-source FAN-OUT and independent-source reads. Do not apply conclusions from one path to the other.
- Keep hard memory and backpressure bounds. Do not introduce replay/spool, extra source reads, larger blocks, deeper queues or new scheduling modes without a verified design and owner-approved behavior change where applicable.
- UI progress is derived from the minimum Written count across active destinations by design; it is not proof of each destination's physical throughput.

## Evidence before optimization
- Inspect current implementation and `CopyJob.DiagnosticsSnapshot` first. Relevant signals include IndependentSourceReads, SourceRead5sBytesPerSecond, BufferWaitTime, PeakBufferedBytes, DeviceSchedulers, FanoutWaitTime, SourceHashTime, WriteTime, DurableFlushTime, CommitTime and verification metrics where present.
- Read `TESTING.md` and `docs/FANOUT-PERFORMANCE-ROADMAP.md` before changing the engine or interpreting simulator output.
- Verify Windows/.NET/Win32 behavior against current primary sources: Microsoft Learn API/reference and behavior-change documentation, .NET release notes and API docs, Windows SDK/Win32 documentation, official WinUI/Windows App SDK release notes, and upstream source/tests where useful. Treat blogs, forums and device marketing claims as leads, not authority.
- Check the API's supported OS/SDK range, target framework, package version, threading/lifetime rules, error semantics and compatibility before adopting it.
- Build a source-to-code gap record: documented contract -> current code path -> evidence/measurement -> proposed minimal change -> regression/acceptance test.
- Every investigation must end in a verified defect, a justified implementation, or a no-change decision with the reason. Do not leave speculative optimization proposals as if they were findings.

## Measurement protocol
Before implementing a performance change, define:
1. Workload: sequential-large, mixed, small-file, verify, contention or another named scenario.
2. Baseline: exact commit/build, dataset, options, source/destination devices, physical topology and system conditions.
3. Metrics: per-destination and global throughput, elapsed time, diagnostics snapshot, CPU, RAM, buffer/backpressure behavior and verification result as applicable.
4. Repetitions and comparison method; report median/range or variance, not the best run.
5. Regression limits for other representative workloads and memory bounds.

Use physical Windows A/B for claims about real storage throughput, controller behavior, durability or device disconnection. A simulator is useful for exploring bounded models but does not establish physical performance. CI and automated tests establish only the behaviors they execute.

## Known path constraints
- Shared fan-out reads the source once per block and holds reference-counted pool pages until every active destination releases them; a slow destination can retain the shared window and trigger backpressure.
- Independent mode (local NVMe source with 2–16 destinations) uses per-destination source reads/pools with a joint 256 MiB cap; this multiplies source reads and hashing. Confirm the actual mode from diagnostics, not assumptions.
- The current shared pool is 256 MiB; do not increase it speculatively.
- A single I/O per physical device is part of the current scheduler contract. Queue-depth changes require physical evidence and ordering/commit analysis.
- Verify the current implementation before relying on any numeric or architectural statement above; roadmap history can become stale.

## Acceptance
Every performance change must:
1. Preserve all integrity, recovery, cancellation and memory guarantees.
2. Pass focused, neighboring and mandatory tests/stress checks.
3. Show repeatable improvement on physical Windows hardware, or remove a bottleneck directly demonstrated by telemetry without claiming an unmeasured throughput gain.
4. Avoid material regressions in other representative scenarios.
5. Keep bounded memory, backpressure, ordering and safe cancellation.
6. Be reversible and documented in the existing relevant PR/audit with exact SHA and measurement method.

If the benefit is absent, noisy or outweighed by a regression, do not ship the optimization. Prefer a no-change decision over speculative complexity.
