# Decision: Fix 28 Empty Select Boxes in Slicer Profile Editors

**Author:** Ripley (Frontend)
**Date:** 2026-04-16
**Status:** Implemented

## Context

Audit of all select fields across slicer profile editors (process, filament, machine) revealed that 28 of 44 select boxes rendered as empty dropdowns because their enum keys were missing from the `KNOWN_ENUMS` map in `MetadataProfileRenderer.tsx`.

## Decision

Add all missing enum entries to `KNOWN_ENUMS` using authoritative values from OrcaSlicer's `PrintConfig.cpp` source code. Create shared arrays (`INFILL_PATTERNS`, `SURFACE_PATTERNS`) to DRY up repeated option lists. Fix `resolveControlType` to exclude numeric `enum_open` types from select rendering.

## Rationale

- The metadata-driven renderer uses a priority chain: `KNOWN_ENUMS` → `meta.enum_values` → empty array. Most settings have no `enum_values` in metadata, so `KNOWN_ENUMS` is the only source.
- OrcaSlicer's `PrintConfig.cpp` is the authoritative source for enum values, labels, and ordering.
- Enum values use inconsistent formatting (spaces, underscores, title case, numeric strings) — each must match exactly.

## Impact

- All 44 select fields now render with correct options
- No API changes required (pure frontend fix)
- No test changes needed (existing tests continue to pass)
