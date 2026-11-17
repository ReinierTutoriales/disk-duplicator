---
name: precise-coding
description: Use when writing or modifying any code. Requires evidence from the current repository and prevents invented APIs, behaviors and unnecessary changes.
---

# Precise Coding

1. Read the relevant C# source, interfaces, call sites, XAML/resources, tests, contracts and project documentation before editing.
2. Trace every consumer of changed types, methods, events, resource keys, serialized data and public/internal contracts. A compile in one project does not prove callers or persisted state are safe.
3. Never invent .NET, WinUI, Win32, filesystem, storage or device APIs, engine policies, recovery semantics or telemetry. Verify unfamiliar APIs in current official documentation and inspect the repository's actual target framework/package versions.
4. If evidence is incomplete, label the assumption or report the gap. Do not fill it with plausible-sounding behavior.
5. Prefer the smallest complete change that fixes a demonstrated defect or satisfies the explicit acceptance criterion. Avoid speculative refactors, unused helpers, wrappers, defensive branches without a real caller, and comments that merely narrate code.
6. Preserve all copy invariants, recovery compatibility, cancellation safety, memory bounds, device scheduling, existing-file policy, verification optionality and UI behavior unless the owner explicitly changes the contract.
7. Read the relevant tests before editing. A functional fix must have a regression test that would detect the old behavior; retain neighboring assertions and contracts.
8. Use focused edits anchored to unique context. For scripted edits, assert the expected match count and inspect the final diff. Do not blindly replace the first textual match.
9. Chain shell commands with `&&` or fail-fast behavior so a failed step cannot be followed by an unintended commit or mutation.
10. Update documentation only when verified behavior changes; prefer the existing relevant document over creating another.
11. Do not claim compilation, test passage, runtime behavior or performance gains unless the exact check or measurement was performed.

Every added line must be traceable to existing code, an official platform contract, a testable requirement or a clearly labeled minimal assumption.

## Efficient delivery
- Work toward the requested acceptance criterion, not a sequence of preparation-only changes.
- Map the minimum complete vertical slice (core logic, UI/callbacks, resources, persistence and tests) before editing.
- Keep a fix cohesive; separate unrelated behavior, UI redesign, dependency updates and build-system changes unless technically inseparable.
- Run all checks available in the current environment before handing work to CI. Inspect the exact CI head and failed logs when a decision depends on them; distinguish infrastructure failures from product regressions.
- Remove code made dead by the change in the same patch. Avoid repeated status-only updates and repeated polling without a decision to make.
- Report the exact SHA, implementation scope, checks actually run, checks not run and residual risk.

## Professional code
- Match surrounding naming, structure, formatting and comment density.
- Comments explain a non-obvious contract, platform constraint or measured tradeoff, not the next line.
- Do not add logging, abstractions or configuration without a demonstrated operational need.
- Put benchmark method and results in the relevant PR/audit, not as hard-coded claims in source.
