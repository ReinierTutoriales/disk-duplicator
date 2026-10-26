---
name: performance-invariants
description: Use when considering or implementing performance changes. Enforces that optimizations preserve integrity, recovery, and memory limits and are justified by telemetry or physical runs.
---

# Performance & Invariants

- Never sacrifice copy invariants, atomicity, recovery (BLAKE3), fail-closed behavior, cancellation safety, or existing-file policy for speed.
- Use existing telemetry (`CopyJob.DiagnosticsSnapshot`, IndependentSourceReads, SourceRead5sBytesPerSecond, BufferWaitTime, PeakBufferedBytes, DeviceSchedulers) as the primary source for identifying bottlenecks before changing the engine. See `docs/FANOUT-PERFORMANCE-ROADMAP.md`.
- Shared fan-out path: one physical read of source, 256 MiB pool with reference counting, backpressure when pool full (slow destination retains pages). Independent path (NVMe local, 2-16 destinations): per-destination pools with joint 256 MiB cap; multiplies source reads/hash. Do not assume shared-path simulator results apply to independent mode.
- Any performance change must:
  1. Keep all integrity and recovery guarantees.
  2. Pass the mandatory test gate and stress tests.
  3. Show repeatable improvement on physical Windows hardware or remove a telemetry-demonstrated bottleneck.
  4. Not materially degrade other representative scenarios.
  5. Preserve hard memory/backpressure limits (no speculative increase of pool, block size, or queue depth).
- Do not change block sizes, pool budgets, prefetch, flush policies, or introduce spool/replay based only on virtualized benchmarks, device marketing names, or intuition. Measure physical A/B (source alone vs mixed destinations) first.
- UI presentation of progress uses min(Written) across active destinations by design; do not claim it proves per-destination physical rate without telemetry.
- See TESTING.md for the full physical benchmark protocol and bottleneck interpretation. Prefer ETW for UI frame latency under load.
- Follow agent-governance rules.
