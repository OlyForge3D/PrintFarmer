# Bed-Type Override Uses `curr_bed_type` in Overrides

**Date:** 2026-05-31  
**Author:** Ripley  
**Issue:** #339  

## Decision

Bed type overrides are passed as `curr_bed_type` inside the `overrides` object of `slicerProfileJson`, not as a top-level field on `SubmitSliceJobRequest`.

## Rationale

- OrcaSlicer workers already process the `overrides` dict and apply key-value pairs to the slicing config.
- `curr_bed_type` is the OrcaSlicer internal key that controls bed plate selection for temperature profiles.
- No backend DTO changes needed — the existing `slicerProfileJson` → `overrides` pipeline handles it.
- "Inherit from profile" = omit the key entirely (empty string not sent).

## Scope

- `QuickSliceModal.tsx`, `NewSliceJobPage.tsx`
- `BED_TYPE_OPTIONS` exported from `metadataTypes.ts` via settings barrel
