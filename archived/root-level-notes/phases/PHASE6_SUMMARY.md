# Phase 6 Summary – Advanced Slicer Profile Management & Slice Job Integration

## Overview
Phase 6 introduced a robust slicer profile ingestion and management subsystem and began integrating those profiles into the slicing job pipeline. This phase replaces ad‑hoc JSON blobs with structured, deduplicated, and queryable profile records while preserving immutability for submitted jobs.

## Goals Accomplished
- Extended `SlicerProfile` domain entity with: `RawJson`, `MetadataJson`, `Hash`, `IsSystem`, `IsPublic`, richer core parameters.
- Added deterministic parsing & sanitation (volatile key removal, stable ordering, SHA256 hashing) for imported profile JSON.
- Implemented repository logic for hash‑based deduplication (`AddOrUpdateFromImportAsync`) and scoped default profile selection.
- Added REST endpoints: import, export, set-default, and extended listing.
- Created React admin UI (`SlicerProfilesPage.tsx`) for profile import, listing, export, default setting with React Query integration.
- Added navigation entry “Slicer Profiles” (admin only).
- Integrated profile selection into slice jobs backend by supporting `SlicerProfileId` in job submission.

## Data Model Changes
### SlicerProfile (in `infra/Domain/Entities.cs`)
New / extended properties:
- `RawJson`: Sanitized raw slicer profile snapshot.
- `MetadataJson`: Flattened metadata for quick display (layerHeight, infillPercentage, etc.).
- `Hash`: SHA256 digest of `RawJson` for deduplication.
- `IsSystem`: Indicates seeded, immutable profiles.
- `IsPublic`: Visibility to other users.

### SliceJob (in `infra/Domain/SliceJob.cs`)
New properties:
- `SlicerProfileId` (nullable FK)
- `SlicerProfile` navigation
Behavior: When submitting with `SlicerProfileId`, the API snapshots `RawJson` into `SlicerProfileJson` to freeze the configuration even if the profile later changes or is deleted.

## EF Core Mapping Updates
`AppDbContext` slice job configuration now includes:
- `SlicerProfileId` property
- Foreign key to `SlicerProfile` with `OnDelete(SetNull)`
- Index on `SlicerProfileId`

## DTO / API Contract Changes
`SubmitSliceJobRequest` now supports:
- `Guid? SlicerProfileId` (takes precedence over `SlicerProfileJson`)
If neither is specified but no raw JSON provided, backend attempts to resolve the default profile for the requested engine.

## Controller Logic Enhancements
`SliceJobController`:
- Injects `ISlicerProfileRepository`.
- On submit: resolves profile by `SlicerProfileId`; if found overrides `SlicerEngine` and sets `SlicerProfileJson` from `RawJson`.
- Attempts fallback to engine default profile if both `SlicerProfileId` and `SlicerProfileJson` are absent.

## Frontend Integration
### New UI Page
`SlicerProfilesPage.tsx` provides:
- Import form (raw JSON + metadata fields + flags).
- Extended listing table (layer height, infill, material, quality, flags).
- Actions: Set Default, Export.

### Services
`slicerProfilesService.ts` implements calls: `listExtended`, `importProfile`, `exportProfile`, `setDefault`.
`sliceJobService.ts` now includes optional `slicerProfileId` in submission interface.

### Navigation
`Layout.tsx` updated to include an admin-only top-level item: “Slicer Profiles”.

## Hash Deduplication Flow
1. User imports raw JSON.
2. Parsing service sanitizes and normalizes JSON (removes volatile keys like timestamps, reorders deterministically, flattens metadata).
3. SHA256 hash computed from canonical string.
4. Repository checks existing profile by hash: create new or update existing (respecting `IsSystem` + override flag).
5. Returned profile may be user-owned or a system profile if already present.

## Metadata Extraction Examples
Stored in `MetadataJson` (flat object):
- `layerHeight`: number (mm)
- `infillPercentage`: number (0–100)
- `filamentMaterial`: string
- `nozzleTemperature`: number (°C)
- `bedTemperature`: number (°C)
- `printSpeed`: number (mm/s)
- `profileType`: string (e.g. “Quality”, “Speed”)
- `slicerVersion`: string
(Actual keys depend on parsing service; easily extended.)

## Endpoints Overview
Base: `/api/slicer/profiles`
- `GET /extended` – list with condensed metadata
- `POST /import` – import raw JSON
- `GET /{id}/export` – export raw JSON + metadata snapshot
- `POST /{id}/set-default` – mark profile default per slicer engine

Slice job submission: `POST /api/slice` now supports `SlicerProfileId`.

## Immutability Guarantees
- Slice jobs copy profile `RawJson` at submit time.
- Future mutations to profile do not affect queued or processing jobs.
- If profile deleted, job retains snapshot (`SlicerProfileJson` + null FK).

## Migration & Schema Notes
If the environment uses EF Migrations, a new migration is required for:
- Added columns on `SliceJobs` table (`SlicerProfileId`)
- FK + index
For dev environments relying on `EnsureCreated`, dropping/recreating the SQLite database will apply changes automatically. Production environments using relational providers should run:
```
dotnet ef migrations add SliceJobProfileReference
# then apply as usual
```
(Ensure `Microsoft.EntityFrameworkCore.Design` is available.)

## Future Work (Planned / Deferred)
- Frontend slice job submission form with profile dropdown (using `listExtended`).
- Integration test: submit slice job with profile ID and assert snapshot usage.
- Bulk import tool for system seeding (preloading curated profiles).
- Profile diffing endpoint (compare imported vs existing). 
- Per-user default profiles (currently global engine default fallback logic present).

## Edge Cases Considered
- Importing identical raw JSON (hash collision) returns existing profile (updates metadata if allowed).
- System profile override blocked unless `allowSystemOverride = true`.
- Missing profile ID in submission → fallback to provided raw JSON or default engine profile.
- Deleted profile after job submission → job unaffected (snapshot retained).

## Quick Submit Flow (Using Profile ID)
1. UI obtains profile list via `GET /api/slicer/profiles/extended`.
2. User selects profile; UI sends `SubmitSliceJobRequest` with `slicerProfileId`.
3. Backend resolves and snapshots profile; job enters queue with stable configuration.

## Validation Status
- Backend builds with new properties (no compile errors introduced).
- Navigation item present for admins.
- TypeScript service extended; no existing callers broken (optional field).

## Summary
Phase 6 delivers a foundational, production-ready framework for slice profile lifecycle management—import, deduplication, querying, snapshot integration with slice jobs—reducing redundancy and stabilizing job reproducibility. Remaining tasks focus on UI job submission and extended tooling.

---

## UI Component Standardization (Post-Phase 6)

After completing the slicer profile backend and initial UI integration, a comprehensive UI standardization effort was undertaken to ensure consistency, maintainability, and accessibility across the React application.

### Shared Component Library

A standardized component library was created in `src/Web/ReactApp/src/components/ui/`:

**Components Created:**
- **Button** - Variant-based button with sizes, loading states, and icon support
  - Variants: primary, secondary, danger, success, subtle
  - Sizes: sm, md
  - Features: loading state, iconLeft, iconRight
- **Alert** - Type-based alert for success/error/info/warning notifications
  - Types: success, error, info, warning
  - Features: optional title, dismissible
- **FormField** - Consistent label + control + helper/error wrapper
  - Features: required indicator, inline layout, conditional helper/error display
- **Input** - Standardized text input with validation states
  - Features: invalid state, disabled state, focus management
- **Select** - Styled dropdown matching Input styling
  - Features: invalid state, disabled state, consistent focus rings
- **ProgressBar** - Configurable progress indicator with labels
  - Sizes: xs, sm, md
  - Colors: blue, green, purple, red, gray
  - Features: label, percentage display, animated, CSS module for width (no inline styles)

**Documentation:**
- Comprehensive usage guide: `src/Web/ReactApp/UI_COMPONENTS_GUIDE.md`
- Color system reference: `src/Web/ReactApp/COLOR_SYSTEM_GUIDE.md`
- Updated README: `src/Web/ReactApp/README.md`

### Design Token Migration

All UI components and pages were migrated from raw Tailwind colors to PrintFarmer design tokens (`pf-*` classes):

**Token Categories:**
- **Backgrounds**: `pf-bg-0`, `pf-bg-1`, `pf-bg-2`, `pf-panel`
- **Text**: `pf-text-primary`, `pf-text-secondary`, `pf-text-muted`
- **Borders**: `pf-border`, `pf-border-light`, `pf-border-medium`
- **Semantic**: `pf-accent`, `pf-success`, `pf-error`, `pf-warning`
- **States**: `pf-disabled`, `pf-status-online-*`, `pf-status-offline-*`
- **Gradients**: `pf-gradient-primary-*`, `pf-gradient-secondary-*`, etc.

**Migration Completed:**
- Replaced all `blue-600`, `gray-*`, `red-*`, `green-*`, `white` with corresponding `pf-*` tokens
- Standardized focus rings to `pf-accent`
- Unified disabled state styling with `pf-disabled`
- Consistent card backgrounds using `pf-panel`
- Text hierarchy respects `pf-text-primary` → `pf-text-secondary` → `pf-text-muted`

### Pages Refactored

Three major pages were refactored to use shared components and design tokens:

1. **NewSliceJobPage.tsx** - Slice job submission form
   - Model picker toggle with localStorage persistence
   - Capabilities JSON validation
   - Profile selection dropdown
   - All form controls use FormField + Input/Select
   - Submit/cancel buttons use Button component
   - Error/success messages use Alert component

2. **SlicerProfilesPage.tsx** - Slicer profile admin UI
   - Import form with FormField/Input/Select
   - Extended table with `pf-bg-1` headers
   - Status badges with `pf-accent-bg`/`pf-success-bg`
   - Error/success alerts
   - Export/delete actions with Button variants

3. **JobQueueDashboardPage.tsx** - Job queue monitoring
   - ProgressBar component for processing jobs
   - Filter buttons with `pf-accent`/`pf-bg-1` styling
   - Job cards with `pf-panel` backgrounds
   - Consistent padding (p-4) and borders (`pf-border`)

### Accessibility Improvements

- **ARIA Labels**: Added to all unlabeled form controls (checkboxes, selects)
- **Focus Management**: Consistent focus rings with `pf-accent`
- **Error Announcements**: `role="alert"` on error messages
- **Semantic HTML**: Proper heading hierarchy, fieldsets for grouping
- **Keyboard Navigation**: All interactive elements keyboard accessible

### Code Quality

- **TypeScript**: Comprehensive typing for all component props
- **No Inline Styles**: Eliminated inline styles (CSS module for ProgressBar width)
- **clsx Usage**: Consistent className composition pattern
- **Prop Spreading**: Proper forwarding of HTML attributes
- **Composition**: FormField wraps inputs for consistent layout

### Developer Experience

- **Reusable Components**: Eliminates copy-paste of button/input/alert markup
- **Type Safety**: Full TypeScript autocomplete for component props
- **Consistency**: Enforces design system usage automatically
- **Documentation**: Comprehensive guides with code examples
- **Migration Path**: Clear guidance for converting raw elements to shared components

### Benefits

- **Visual Consistency**: All UI elements follow PrintFarmer design system
- **Maintainability**: Changes to Button/Input/Alert propagate everywhere
- **Accessibility**: Built-in ARIA attributes and focus management
- **Theme Support**: Ready for light/dark mode switching via `pf-*` tokens
- **Developer Velocity**: Faster feature development with component library
- **Code Quality**: Reduced duplication and inline styling

### Next Steps

Future component library enhancements:
- RadioGroup, Checkbox, Textarea components
- Modal/Dialog component
- Badge and tooltip components
- Tabs navigation component
- Card wrapper component
- Storybook setup for component showcase

---
_Last updated: Phase 6 implementation + UI component standardization (2025-10-19)._
