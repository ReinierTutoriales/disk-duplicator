---
name: testing-discipline
description: Use for functional changes, bug fixes, core logic, recovery, FAN-OUT and WinUI behavior. Requires regression evidence and exact-SHA validation.
---

# Testing Discipline

## Before changing code
- Identify the observable behavior, affected contracts, existing coverage and the smallest test that would fail on the old defect.
- For a confirmed bug, reproduce it or establish it from source/logs; add a regression assertion that detects the old behavior and proves the correction.
- Test adjacent invariants and failure paths. Do not weaken, delete or skip an assertion to obtain a green run.
- Classify failures before acting: product logic, test fixture, build/configuration, CI infrastructure or environment. Read the evidence; do not change unrelated code to silence a failure.

## Mandatory automated gate
Run the complete gate in `TESTING.md`:
```powershell
dotnet restore dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release --no-restore
dotnet restore dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -r win-x64
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64 --no-restore
```
For UI changes, run the built self-contained EXE with `--layout-check report.json`; require exit code 0 and `Passed=true`. Include the relevant preparation/execution states, light/dark themes, supported DIP sizes, breakpoint behavior and dialogs.

## Regression coverage
- Select tests by changed contract: tree structure/empty directories, single-file scope, FAN-OUT multi-block delivery, Unicode/Windows paths, overlap and reparse-point rejection, preflight/space, atomic replacement, recovery journal/manifest/BLAKE3, pause/resume/cancel, isolated destination failure, backpressure/reference release, verification/CRC32C and telemetry.
- For UI, cover layout, theme, focus/keyboard, accessibility labels, dialogs/owner modality, window sizing, DPI conversion, minimized refresh and progress updates without real I/O.
- For recovery or storage changes, include interrupted writes/commits, stale state, partial files, destination lease, device failure and safe restart cases where relevant.
- Inspect tracked paths and the complete diff before pushing. Exclude generated caches and unrelated files.

## Hardware evidence
Automated tests and simulators do not prove physical storage throughput, controller behavior, USB disconnect recovery, filesystem durability or ETW frame timing. Use the physical protocol in `TESTING.md` for claims that depend on those properties. Do not block source analysis, code corrections, static checks or automated tests merely because physical hardware is unavailable; label the exact residual validation gap.
For performance claims, compare equivalent datasets, source/destination topology, options, build and system conditions; repeat runs and report per-destination/global throughput, diagnostics, CPU/RAM and variance. Never report the best run alone.

## CI and delivery
- Require CI success on the exact commit SHA being integrated. A previous green run does not validate a newer commit.
- Report local tests, CI, layout-check and physical Windows validation as separate evidence categories.
- A build is not a runtime test; a layout-check uses synthetic progress and does not test actual storage I/O.
- A merged commit, a passing CI run, a started release workflow and a published signed artifact are different states. Verify the tag, workflow conclusion and assets before claiming publication.
- Documentation-only changes need diff/link/coherence checks, not artificial code tests. State the checks actually performed and any unavailable checks.
