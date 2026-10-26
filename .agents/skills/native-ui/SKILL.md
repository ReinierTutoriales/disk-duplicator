---
name: native-ui
description: Use for WinUI 3, XAML, or any interface work. Ensures native, polished, accessible results consistent with the existing compact design and verified UI optimizations.
---

# Native UI

- Use only stable, documented WinUI 3 and Windows APIs. Prefer latest stable controls and patterns; avoid unnecessary third-party UI libraries.
- Keep the result polished: correct states, keyboard/focus, DPI awareness, theme support (light/dark), accessibility (Narrator), and consistency with the existing compact layout and Desktop Acrylic (fallback solid).
- Respect existing UI contracts and layout checks (`--layout-check` must pass). Do not break adaptive layout (<640 DIP narrow mode) or theme resources.
- Progress updates: maintain 250 ms foreground / 2 s minimized refresh. Deduplicate text and property assignments. Avoid layout passes on identical values. Prefer ItemsRepeater/ListView virtualization for destination rows when N is high (ResultsAsync currently materializes up to 256; measure before changing).
- Present existing data only: per-destination BytesPerSecond, BusType/MediaKind, read mode (shared vs independent from DiagnosticsSnapshot), phase indicator (copy vs verification), ETA that reanchors timestamp after pause. Do not invent new telemetry reads.
- Taskbar progress, power request (prevent idle sleep), process speed limit on minimize, and notification on completion already exist; preserve them.
- Note what requires physical visual validation on Windows (Narrator, high contrast, multi-DPI, real Acrylic composition, frame times under load via ETW/XAML Frame Analysis).
- Follow agent-governance: no AI-named branches, owner-only authorship, verify before write, minimal change.
