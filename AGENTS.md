# RepartoCopier — Agent Instructions

## Project
Windows desktop disk duplicator (C# / .NET 10 / WinUI 3). Copies one source to multiple destinations via FAN-OUT while preserving full folder structure, integrity, recovery, and native OS behavior.

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
```
See `TESTING.md` for the full mandatory gate and physical benchmark protocol.

## Priorities
- Preserve all copy invariants (structure, empty folders, atomic replace, fail-closed reparse points, existing-file policy, optional CRC32C verification, recovery BLAKE3, etc.).
- Prefer native WinUI / Windows APIs. Keep architecture simple and measurable.
- Do not invent engine semantics, policies, or APIs not present in the code or docs.
- Do not relax tests or architecture contracts.
- Performance changes must keep integrity/recovery guarantees and show real improvement via telemetry or physical Windows runs (see `TESTING.md`).
- Prefer latest stable versions; evaluate regression risk before updating.

## Working style
1. Brief analysis + risks.
2. Propose the optimal grounded solution (compare only if clearly better).
3. Implement the smallest correct change.
4. Verify against existing code, tests, and contracts.
5. State what is measured vs. what needs physical Windows validation.

## Hard Rules for All Agents (mandatory)
- **Branches**: Never create or push branches whose name contains an AI name, model name, tool name, or any non-owner identifier (examples forbidden: `claude-*`, `gpt-*`, `grok-*`, `ai-*`, `agent-*`, `copilot-*`). Use only descriptive, owner-neutral names (`fix-eta-pause`, `ui-phase-indicator`, etc.).
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

## Skills
Use skills from `.agents/skills/` when relevant, especially `precise-coding` and `agent-governance` for any code or documentation change.
