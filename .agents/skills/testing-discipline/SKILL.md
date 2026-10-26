---
name: testing-discipline
description: Use when adding features, fixing bugs, or changing core/UI logic. Enforces the mandatory test gate and no relaxation of contracts.
---

# Testing Discipline

- Run or reason about the mandatory gate:
  ```powershell
  dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
  ```
- For UI changes, also run the self-contained EXE with `--layout-check report.json` (preparation/execution, light/dark, multiple DIP sizes including breakpoint around 640, dialogs). Must pass with 0 exit and Passed=true.
- Do not relax, skip, or weaken existing tests or architecture contracts.
- Prefer focused tests for changed behavior (especially invariants, recovery, FAN-OUT, verification, presentation helpers).
- A change is incomplete until consistent with the test baseline and CI expectations.
- Explicitly call out gaps that still need physical Windows validation (see TESTING.md): real USB/SATA/NVMe throughput A/B, disconnection/cancellation under load, ETW frame analysis, Narrator/high-contrast/multi-DPI, Acrylic composition.
- Follow agent-governance rules on branches and authorship.
