---
name: code-quality
description: Use when optimizing, cleaning, reviewing, or polishing code. Prioritizes simplicity, maintainability, performance, and production readiness while preserving invariants.
---

# Code Quality

- Analyze for real improvements in clarity, performance, robustness, and maintainability.
- Prefer the simplest correct solution that matches existing style.
- If a superior alternative exists, compare briefly and choose the most production-solid one; state what improved and why.
- Do not break copy invariants, recovery guarantees, or architecture contracts.
- Keep changes focused and measurable.
- Verify against existing tests and contracts.
- Note any remaining need for physical Windows validation.
