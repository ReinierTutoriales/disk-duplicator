# RepartoCopier — Agent Instructions

## Project
Windows desktop disk duplicator (C# / .NET 11 / WinUI 3). Copies one source to multiple destinations via FAN-OUT while preserving full folder structure, integrity, recovery, and native OS behavior. Version 2.2.4+ on main.

## Stack
- C# / .NET 11 (SDK 11.0.100+)
- WinUI 3 + Windows App SDK 2.6
- Solution: `RepartoCopier.sln`
- Core: `dotnet/RepartoCopier.Core`
- UI: `dotnet/RepartoCopier.WinUI`
- Tests: `dotnet/RepartoCopier.Core.Tests`

## Commands
```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
# Layout verification (synthetic progress, no real I/O):
# RepartoCopier.WinUI.exe --layout-check report.json
```
See `TESTING.md` for the full mandatory gate and physical benchmark protocol. Key docs: `docs/UI-AUDIT-2026-10-15.md`, `docs/UI-RESPONSIVENESS-2026-10-16.md`, `docs/FANOUT-PERFORMANCE-ROADMAP.md`, `docs/NATIVE-MATERIALS-AND-ICONS.md`, `docs/FINAL-REVIEW-2026-10-17.md`.

## Priorities
- Preserve all copy invariants (structure, empty folders, atomic replace, fail-closed reparse points, existing-file policy fixed to metadata-diff replace, optional CRC32C verification, recovery BLAKE3, etc.).
- Prefer native WinUI / Windows APIs. Keep architecture simple and measurable.
- Do not invent engine semantics, policies, or APIs not present in the code or docs.
- Do not relax tests or architecture contracts.
- Performance changes must keep integrity/recovery guarantees and show real improvement via telemetry (`CopyJob.DiagnosticsSnapshot`, IndependentSourceReads, SourceRead5sBytesPerSecond, BufferWaitTime, PeakBufferedBytes) or physical Windows runs (see `TESTING.md` and FANOUT roadmap).
- **Mandatory stable updates (owner decision):** whenever a newer official stable release is available for a component, dependency, SDK or build/CI tool used by this project, update it without asking for routine approval. Check official release notes, compatibility and migration requirements; update lockfiles/checksums/provenance where applicable and verify the exact resulting SHA. Do not substitute preview, beta, RC or nightly builds. Resolve reproducible incompatibilities and report concrete blockers; do not silently retain an older stable version. This does not authorize unrelated behavior changes or release publication.
- UI: 250 ms refresh (2 s minimized), deduplicated text, layout mode change only on breakpoint, Desktop Acrylic (optional Mica discussion), per-destination progress, taskbar progress, power request, speed limit on minimize. Prefer presenting existing telemetry (BytesPerSecond, BusType/MediaKind, read mode) over new reads.

## Working method
1. Establish the exact repository, branch, base SHA, current working tree, relevant PRs and CI state before relying on historical reports.
2. State the observed issue or requested outcome, affected contracts, evidence and material risks. Separate confirmed defects from hypotheses.
3. Read relevant source, all consumers, tests, architecture contracts and current project documentation. Consult the applicable skill before implementation.
4. Contrast design choices with current official Microsoft/.NET/Windows/WinUI documentation and the actual APIs/dependency versions in use. Prefer primary sources over blogs, forum posts or unsourced benchmark claims.
5. Choose the smallest complete implementation that meets the acceptance criterion. Keep one active implementation path; avoid speculative refactors and duplicate helpers.
6. Add regression coverage for confirmed defects and run focused tests plus neighboring suites and the mandatory gate.
7. Verify the exact resulting SHA in CI. Report automatic checks, measurements and physical Windows checks separately.
8. Record only durable findings in the relevant existing skill or project contract. Do not create duplicate process documents.

## Evidence and validation
- Use this evidence order: official platform/API contract; official implementation/source/tests; this repository's code and tests; CI and reproducible automated measurements; physical Windows hardware where the claim depends on real storage, controller, display or input behavior.
- Distinguish **documented/correct by contract**, **automatically verified**, and **empirically measured**. A successful build does not prove runtime behavior; CI does not prove physical throughput; a simulator does not prove a physical storage result.
- Do not block source review, static analysis, architecture work, documentation corrections or automated tests on physical hardware. Physical Windows A/B is required to claim a throughput improvement; real device tests are required for hardware-specific behavior such as USB disconnects, controller timeouts, ETW frame latency, Narrator, high contrast, DPI and Acrylic composition.
- Never claim a performance improvement without comparable measurements. Define scenario, baseline, hardware/topology, dataset, options, repetitions and metrics; report variance and regressions, not only the best run.
- For UI changes, preserve the 250 ms foreground / 2 s minimized refresh policy, deduplicate state updates and avoid layout passes when values have not changed.

## Hard Rules for All Agents (mandatory)
- **Branches**: Never create or push branches whose name contains an AI name, model name, tool name, or any non-owner identifier (examples forbidden: `claude-*`, `gpt-*`, `grok-*`, `ai-*`, `agent-*`, `copilot-*`). Use only descriptive, owner-neutral names.
- **Authorship / signing**: Never commit, sign, or set author/committer to any name, email, or identity that is not the repository owner (`ReinierTutoriales`). Do not use AI names, tool names, or third-party identities in commit messages, trailers, or metadata.
- **Write only what is verified**: Read the relevant files, tests, contracts, and documentation before proposing or writing any change. Do not write code, comments, or docs for the sake of writing. Every addition must be the minimal change that solves a demonstrated need and is traceable to existing code or a clearly labeled assumption.
- **Documentation**: Update or create documentation only when it reflects verified current behavior. Do not invent APIs, policies, or guarantees. Prefer updating existing docs over creating new ones. Keep docs short, precise, and consistent with the code.
- **Optimal code**: Prefer the simplest correct solution that matches existing style and patterns. Avoid speculative refactors, unused helpers, extra abstractions, or "improvements" without measured benefit. Verify against tests and contracts before considering the change complete.

## Rules
- Match existing style and patterns.
- Keep changes focused; do not touch unrelated files.
- One active implementation line.
- Prefer the simplest correct fix.
- Before changing CI or release workflows, read the relevant files carefully.
- UI changes must not alter engine policies (existing-file policy, read mode selection, pools, verification optionality).
- Ask the owner before implementing unrequested product behavior changes, merging, publishing releases or deleting branches.
- Do not promise zero regressions. State residual risk and unverified behavior.

## Skills
Use the relevant instructions in `.agents/skills/`:
- `precise-coding` for all implementation.
- `testing-discipline` for functional changes and regression coverage.
- `code-quality` for reviews, cleanup and refactors.
- `agent-governance` before branch, commit, PR, documentation or release mutations.
- `native-ui` for WinUI/XAML, accessibility, focus, layout and visual behavior.
- `performance-invariants` for I/O, FAN-OUT, memory, throughput, scheduling and verification changes.

The Android-specific `tv-ui`, WebView optimization and Android source-migration skills from VireoLumaTV are intentionally not imported: their platform APIs and acceptance criteria do not apply to this Windows product. Their reusable evidence, regression and minimal-change methods are incorporated into the Windows skills above.

## Session continuity
- On resuming work, query the remote repository state again: default branch, exact SHA, open PRs, CI and latest release when relevant. Prior chat summaries are context, not proof.
- Discover which tools can actually read, edit, build, test, run workflows or inspect artifacts. Never claim local execution or physical validation when only source/CI inspection was available.
- If work remains incomplete, report repository, branch, exact SHA, PR, checks run/not run, blocker and next action. Re-check the remote state in the next session.
- Do not expose credentials, signing keys or secret values in code, logs, commits or reports.
