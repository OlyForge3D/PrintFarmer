# Decision: Advanced Settings Disclosure Pattern

**Date:** 2026-05-31  
**Author:** Ripley  
**Issue:** #340  

## Context

NewSliceJobPage exposed all 344 process settings inline, creating noise for preset-based workflows.

## Decision

Wrap raw parameter panel in `AdvancedSettingsDisclosure` (uses existing `CollapsibleSection`). Collapsed by default with localStorage persistence. Override count shown when collapsed. Preset dropdowns remain always-visible.

## Implications

- Future parameter panels on other pages can reuse the same pattern/component.
- `pf.slicer.advancedDisclosure` localStorage key is now reserved for this purpose.
- The QuickSliceModal (#338) already hides raw settings by design; this makes the full page match that philosophy.
