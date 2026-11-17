---
name: agent-governance
description: Mandatory rules for repository work. Enforces owner identity, authorization, evidence before edits, safe Git operations and accurate delivery reporting.
---

# Agent Governance

These rules apply to every agent and every repository mutation.

## Branches
- Use short, descriptive, owner-neutral branch names that identify the change.
- Never create, push or reference branches containing an AI/model/tool name or non-owner identity: `claude-*`, `gpt-*`, `grok-*`, `ai-*`, `agent-*`, `copilot-*`, `cursor-*`, or equivalent.
- Do not modify `main` directly for ordinary work. Start from the current remote default branch and preserve unrelated work.

## Authorship and signing
- Commit author, committer and signature identity must remain the repository owner, `ReinierTutoriales`.
- Never add AI/tool names or third-party identities to commit messages, trailers (`Signed-off-by`, `Co-authored-by`), PR descriptions, comments or file metadata.
- Commit messages describe the actual change; never attribute it to an agent.
- Before a local commit, inspect Git identity and signing configuration. Do not allow an environment's global signing key or identity to silently override the repository owner's identity.

## Owner authorization
- Updating a used dependency, SDK or build tool to a newer official stable release is standing authorization. Check compatibility, migration notes and exact build/test results; resolve reproducible breakage and report blockers. Do not use preview, beta, RC or nightly versions as stable replacements.
- This standing authorization does not permit unrelated product behavior changes, changing copy policy/invariants, publishing a release or merging a PR.
- Ask the owner before introducing or changing product behavior not explicitly requested.
- Merging PRs, publishing releases, closing PRs and deleting branches require explicit owner authorization.
- Preserve decisions and UX contracts already made by the owner until the owner changes them.

## Verify before writing
- Read the relevant source, tests, architecture contracts, dependency versions and existing documentation before proposing or implementing a change.
- Every added line must solve a demonstrated need or satisfy an explicit requirement. Label assumptions and unknowns.
- Never weaken tests, safety checks, integrity guarantees or architecture constraints to make a change pass.
- Before changing CI or release workflows, inspect the complete relevant workflow and its permissions, triggers, artifacts and signing path.

## Documentation
- Document verified current behavior only. Do not invent APIs, policies, guarantees, benchmark results or future plans.
- Prefer updating the existing README, TESTING.md, audit or roadmap rather than creating duplicate documents.
- Keep public product copy focused on product identity, user benefits and download/use guidance. Keep engineering instructions in AGENTS.md and skills.

## Optimal implementation
- Prefer the simplest correct implementation matching existing architecture.
- Avoid speculative refactors, parallel implementations, unused helpers and changes without demonstrated benefit.
- Preserve a single authoritative implementation path.
- A change is incomplete until checked against relevant tests, contracts and UI layout validation where applicable.

## Remote jobs and continuity
- After pushing or dispatching a workflow, do not repeatedly wait or poll without a decision that depends on the result. Report the exact SHA, PR/workflow link and pending state; inspect status when the owner asks or when integration/release decisions require it.
- When resuming, query the repository's current remote state. Previous conversation reports do not prove the current branch, SHA, CI or release state.
- If a session ends with pending work, report repository, branch, SHA, PR, checks run/not run, blocker and the next concrete action.

## Learn from verified regressions
- Classify findings as confirmed defect, exposure with explicit preconditions, or unverified hypothesis.
- For a confirmed regression, establish the root cause from source, logs or reproduction; fix it and add regression coverage before adding a permanent lesson.
- Add one concise reusable rule to the relevant existing skill only when it prevents recurrence. Update or remove obsolete advice rather than accumulating incident diaries.
- Keep product contracts in project documentation and incident evidence in the relevant audit/PR, not duplicated across every skill.
