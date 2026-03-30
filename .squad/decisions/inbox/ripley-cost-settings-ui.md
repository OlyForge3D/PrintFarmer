# Decision: Cost Tracking Settings UI — No Custom Section Needed

**Author:** Ripley (Frontend Dev)
**Date:** 2026-07-08
**Status:** Implemented

## Context

Task requested adding a "Cost Tracking" section to the admin Settings page with manual field definitions (toggle, number inputs with ranges, helper text, validation).

## Finding

The Settings page is **metadata-driven**. `CostTrackingSettings.cs` already has all required backend attributes:
- `[AppSetting("CostTracking")]` — auto-discovered by `SettingsService`
- `[SettingGroup("Operations")]` — appears under "Operations" in sidebar
- `[SettingDisplay]` on each property — labels, descriptions, input types, min/max ranges
- `IValidatableSetting` — server-side validation on save

The `SettingsPagelet` component renders these dynamically. No per-section frontend code is needed.

## What Was Done

1. **Verified** CostTracking already renders in the Settings UI via the metadata system
2. **Added** `CostTrackingSettings` TypeScript interface in `api.ts` for type-safe access from cost features
3. **Added** `getCostTrackingSettings()` / `updateCostTrackingSettings()` convenience methods on apiClient
4. **Added** 7 focused tests verifying CostTracking metadata renders correctly (toggle, numbers, values, onChange, validation errors, tooltips)

## For Lambert (Backend)

No backend changes needed — `CostTrackingSettings` is already fully wired. The attributes, validation, and persistence all work through the existing `UnifiedSettingsController` + `SettingsService` pipeline.

## Files Changed

- `src/Web/ReactApp/src/types/api.ts` — added `CostTrackingSettings` interface
- `src/Web/ReactApp/src/services/api.ts` — added typed convenience methods
- `src/Web/ReactApp/src/test/components/CostTrackingSettingsPagelet.test.tsx` — new test file (7 tests)
