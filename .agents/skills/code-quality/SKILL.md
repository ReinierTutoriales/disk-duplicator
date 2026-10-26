---
name: code-quality
description: Use when optimizing, cleaning, reviewing, or polishing code. Prioritizes simplicity, maintainability, performance, and production readiness while preserving invariants.
---

# Code Quality

- Analyze for real improvements in clarity, performance, robustness, and maintainability.
- Prefer the simplest correct solution that matches existing style.
- If a superior alternative exists, compare briefly and choose the most production-solid one; state what improved and why.
- Do not break copy invariants, recovery guarantees, architecture contracts, UI refresh throttle (250 ms / 2 s), layout adaptive logic, or telemetry contracts.
- Keep changes focused and measurable. Do not write for the sake of writing.
- Verify against existing tests, contracts, and (for UI) `--layout-check` before considering the change complete.
- Update documentation only when it reflects verified current behavior. Prefer editing existing docs (UI audits, FANOUT roadmap) over creating new ones.
- Note any remaining need for physical Windows validation.
- Follow agent-governance rules on branches and authorship.
