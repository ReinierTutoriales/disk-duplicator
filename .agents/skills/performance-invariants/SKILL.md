---
name: performance-invariants
description: Use when considering or implementing performance changes. Enforces that optimizations preserve integrity, recovery, and memory limits and are justified by telemetry or physical runs.
---

# Performance & Invariants

- Never sacrifice copy invariants, atomicity, recovery (BLAKE3), fail-closed behavior, or cancellation safety for speed.
- Use existing telemetry (`CopyJob.DiagnosticsSnapshot`) as the primary source for identifying bottlenecks before changing the engine.
- Any performance change must:
  1. Keep all integrity and recovery guarantees.
  2. Pass the mandatory test gate and stress tests.
  3. Show repeatable improvement on physical Windows hardware or remove a telemetry-demonstrated bottleneck.
  4. Not materially degrade other representative scenarios.
  5. Preserve hard memory/backpressure limits.
- Do not change block sizes, pool budgets, prefetch, or flush policies based only on virtualized benchmarks or device marketing names.
- See TESTING.md for the full physical benchmark protocol and bottleneck interpretation.
