---
name: code-quality
description: Use when reviewing, optimizing, cleaning or refactoring code. Prioritizes correctness, simplicity, maintainability and measured production value.
---

# Code Quality

- Review the real implementation, callers, tests, contracts, diagnostics and current official platform guidance before proposing a change.
- Prefer the simplest correct solution that fits the existing architecture and naming/style.
- Compare alternatives only when there is a credible production benefit. State the tradeoff and choose one implementation; do not preserve parallel paths without a demonstrated requirement.
- Fix root causes rather than symptoms. Separate confirmed defects from speculative risks.
- Preserve copy semantics, structure, empty folders, atomic replacement, fail-closed path handling, recovery/BLAKE3, cancellation safety, existing-file policy, optional CRC32C verification, FAN-OUT behavior and memory/backpressure limits.
- Preserve UI contracts: native WinUI, light/dark theme, DPI/work-area sizing, focus/keyboard, Narrator labels, 250 ms foreground / 2 s minimized refresh, deduplicated updates and adaptive layout.
- Prefer existing telemetry and APIs over new reads, dependencies, configuration or abstractions.
- Do not change block size, pool budget, queue depth, prefetch, flush policy, scheduler or storage mode based on intuition, virtualized benchmarks or device marketing specifications.
- Keep changes focused and complete. Remove code made dead by the change; do not add helpers or comments without a real consumer or contract.
- Verify against existing tests and project contracts. For UI, run `--layout-check`; for performance, follow `performance-invariants` and the physical A/B protocol.
- A smaller diff, newer API, fewer lines or successful compilation alone does not prove improved performance or quality.
- Update documentation only when verified behavior changes; prefer the existing audit, roadmap or TESTING.md.
- Follow `agent-governance` for all Git and release actions.
