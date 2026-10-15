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

## Rules
- Match existing style and patterns.
- Keep changes focused; do not touch unrelated files.
- One active implementation line.
- Prefer the simplest correct fix.
- Before changing CI or release workflows, read the relevant files carefully.

## Skills
Use skills from `.agents/skills/` when relevant, especially `precise-coding` for any code change.
