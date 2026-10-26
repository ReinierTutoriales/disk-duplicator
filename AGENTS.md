# RepartoCopier — Agent Instructions

## Project
Windows desktop disk duplicator (C# / .NET 10 / WinUI 3). Copies one source to multiple destinations via FAN-OUT while preserving full folder structure, integrity, recovery, and native OS behavior. Version 2.2.4+ on main.

## Stack
- C# / .NET 10 (SDK 10.0.100+)
- WinUI 3 + Windows App SDK 2.5
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
- Prefer latest stable versions; evaluate regression risk before updating.
- UI: 250 ms refresh (2 s minimized), deduplicated text, layout mode change only on breakpoint, Desktop Acrylic (optional Mica discussion), per-destination progress, taskbar progress, power request, speed limit on minimize. Prefer presenting existing telemetry (BytesPerSecond, BusType/MediaKind, read mode) over new reads.

## Working style
1. Brief analysis + risks (UI frame time, FAN-OUT backpressure, recovery).
2. Propose the optimal grounded solution (compare only if clearly better).
3. Implement the smallest correct change.
4. Verify against existing code, tests, contracts, and `--layout-check` when UI-related.
5. State what is measured vs. what needs physical Windows validation (ETW/XAML Frame Analysis for frames, physical A/B for throughput).

## Hard Rules for All Agents (mandatory)
- **Branches**: Never create or push branches whose name contains an AI name, model name, tool name, or any non-owner identifier (examples forbidden: `claude-*`, `gpt-*`, `grok-*`, `ai-*`, `agent-*`, `copilot-*`). Use only descriptive, owner-neutral names (`fix-eta-pause`, `ui-phase-indicator`, `show-dest-speed`).
- **Authorship / signing**: Never commit, sign, or set author/committer to any name, email, or identity that is not the repository owner (`ReinierTutoriales`). Do not use AI names, tool names, or third-party identities in commit messages, trailers, or metadata.
- **Write only what is verified**: Read the relevant files, tests, contracts, and documentation before proposing or writing any change. Do not write code, comments, or docs for the sake of writing. Every addition must be the minimal change that solves a demonstrated need and is traceable to existing code or a clearly labeled assumption.
- **Documentation**: Update or create documentation only when it reflects verified current behavior. Do not invent APIs, policies, or guarantees. Prefer updating existing docs over creating new ones. Keep docs short, precise, and consistent with the code.
- **Optimal code**: Prefer the simplest correct solution that matches existing style and patterns. Avoid speculative refactors, unused helpers, extra abstractions, or "improvements" without measured benefit. Verify against tests and contracts before considering the change complete. For UI, preserve 250 ms throttle and avoid layout passes on identical values.

## Rules
- Match existing style and patterns.
- Keep changes focused; do not touch unrelated files.
- One active implementation line.
- Prefer the simplest correct fix.
- Before changing CI or release workflows, read the relevant files carefully.
- UI changes must not alter engine policies (existing-file policy, read mode selection, pools, verification optionality).

## Skills
Use skills from `.agents/skills/` when relevant, especially `precise-coding`, `agent-governance`, `native-ui`, and `performance-invariants` for any code or documentation change.
