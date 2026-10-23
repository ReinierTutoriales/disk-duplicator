---
name: agent-governance
description: Mandatory rules for all agents. Enforces branch naming, authorship, verification before writing, and optimal documentation. Use before any commit, branch, or documentation change.
---

# Agent Governance

Hard constraints that override convenience:

## Branches
- Never create, push, or reference branches whose name contains an AI name, model name, tool name, or non-owner identifier.
- Forbidden patterns: `claude-*`, `gpt-*`, `grok-*`, `ai-*`, `agent-*`, `copilot-*`, `cursor-*`, or any similar.
- Use only short, descriptive, owner-neutral names that describe the change (`fix-eta-after-pause`, `show-destination-speed`).

## Authorship and signing
- Never set author, committer, or any signature to a name, email, or identity other than the repository owner (`ReinierTutoriales`).
- Do not insert AI names, tool names, or third-party identities into commit messages, trailers (`Signed-off-by`, `Co-authored-by`), or file metadata.
- Commit messages must describe the change factually; do not attribute the work to an agent.

## Verify before writing
- Read the relevant source, tests, contracts, and existing documentation before proposing or writing any change.
- Do not write code, comments, or documentation for the sake of writing.
- Every line added must be the minimal change that solves a demonstrated need and is traceable to existing code or a clearly labeled, minimal assumption.
- If something is unclear, state the gap explicitly instead of inventing behavior.

## Documentation
- Update documentation only when it reflects verified current behavior.
- Do not invent APIs, policies, guarantees, or future plans.
- Prefer editing existing docs over creating new files.
- Keep documentation short, precise, and consistent with the code and tests.

## Optimal code
- Prefer the simplest correct solution that matches existing style and patterns.
- Avoid speculative refactors, unused helpers, extra abstractions, or changes without measured benefit.
- A change is incomplete until it has been verified against existing tests and contracts.

These rules apply to every agent and every change.
