# Decision: Location Components Canonical Location

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-08
**Status:** IMPLEMENTED

## Decision

All location UI components now live in `src/Web/ReactApp/src/features/locations/components/`:
- `LocationTreePicker.tsx` — tree dropdown for selecting locations
- `LocationBreadcrumb.tsx` — ancestor path display
- `LocationSelector.tsx` — wrapper around TreePicker for backward compat
- `LocationManagement.tsx` — full CRUD tree management page

## Rationale

Previously scattered across `common/components/` and `features/catalog/components/`. Consolidating under `features/locations/` follows the feature-folder convention and makes the location feature self-contained.

## Migration

Re-export shims remain at old paths (`common/components/LocationTreePicker.tsx`, etc.) so existing imports don't break. New code should import from `@/features/locations/components/`.

## Types

All location types are now defined in `@/types/api.ts` (canonical) and re-exported from `@/services/locationService.ts` for backward compat. The API client methods are fully typed — no more `Record<string, unknown>`.
