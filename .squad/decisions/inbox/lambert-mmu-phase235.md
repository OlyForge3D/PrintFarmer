# FlashForge MMU Phases 2, 3, 5 Implementation

**Author:** Lambert  
**Date:** 2026-07-16  
**Branch:** `feature/multi-toolhead-filament-tracking`  
**Status:** Committed (cf534896)

## Decision

Implemented FlashForge multi-material support across three phases:

### Phase 2: Extruder Count Detection
- `GetCompositeStatusAsync` now calls M115 every poll cycle alongside M105
- `DetectExtruderCount` cross-references M115 Tool Count with M105 extruder count, takes MAX
- Addresses ADX5 firmware bug where Tool Count: 1 but T0+T1 are reported in M105
- Adds ~50ms per poll cycle (acceptable at 10-30s intervals)

### Phase 3: MmuGate Auto-Creation & Pipeline Wiring
- `SyncMmuToolheadsOnEntity` creates/removes MmuGate virtual toolheads when MultiMaterial toggled
- Operates on already-loaded Printer entity; caller saves (avoids double-save with `EnsureMmuToolheadsAsync`)
- `PrinterStatusDto` extended with optional `ExtruderTemperatures` and `DetectedExtruderCount` (null defaults)
- FlashForgePollingService wires both fields from `PrinterCompositeStatus` into the DTO

### Phase 5: Per-Extruder Temperature Control
- `ISupportsMultiExtruderTemperatureControl` interface added to capability system
- `IFlashForgeClient` inherits the interface; `FlashForgeClient` implements `SetExtruderTemperatureAsync`
- Sends `M104 S{temp} T{index}` gcode via TCP connection

## Trade-offs
- M115 called every poll cycle (static info) to keep extruder count fresh — could cache with TTL later
- `SyncMmuToolheadsOnEntity` is synchronous entity manipulation, not async — intentional to avoid separate load/save
