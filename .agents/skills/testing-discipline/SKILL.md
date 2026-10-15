---
name: testing-discipline
description: Use when adding features, fixing bugs, or changing core/UI logic. Enforces the mandatory test gate and no relaxation of contracts.
---

# Testing Discipline

- Run or reason about the mandatory gate:
  ```powershell
  dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
  ```
- Do not relax, skip, or weaken existing tests or architecture contracts.
- Prefer focused tests for changed behavior (especially invariants, recovery, FAN-OUT, verification).
- A change is incomplete until consistent with the test baseline and CI expectations.
- Explicitly call out gaps that still need physical Windows validation (see TESTING.md).
