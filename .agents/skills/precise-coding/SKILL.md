---
name: precise-coding
description: Use when writing or modifying any code. Forces grounded output by requiring verification against the existing codebase before inventing anything. Reduces hallucinations and token waste.
---

# Precise Coding (Anti-Hallucination)

1. Read the relevant existing files, types, tests, contracts, and documentation before writing anything.
2. Never invent APIs, function signatures, types, WinUI controls, engine policies, or behaviors not already present or clearly documented.
3. If something is unclear or missing, state the assumption explicitly instead of inventing it.
4. Prefer the smallest change that matches existing patterns and can be verified against tests.
5. Do not claim correctness, compilation, or test passage unless reasoned from the actual project files.
6. Write only the precise code needed. Do not write for the sake of writing. Avoid speculative refactors, unused helpers, extra abstractions, or comments that do not document verified behavior.
7. Update documentation only when it reflects verified current behavior. Prefer editing existing docs over creating new ones.
8. Follow agent-governance rules: no AI-named branches, no authorship other than the repository owner.

Every line must be traceable to existing code or a clearly labeled minimal assumption.
