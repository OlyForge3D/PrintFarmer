# Decision: Fix ImportOfficialProfilesPage data source

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-07-25  
**Status:** Implemented

## Problem

The "Import Official Profiles" page (`/slicer/import-official`) shows wrong profiles for every printer. Selecting vt01 (Voron Trident), SV08 (Sovol), or qp4-1 (QIDI X-Plus 4) all return the same 21 Elegoo Centauri profiles with null material and zero layer height/infill. Users see "undefined • Draft" group headers and "0mm • 0% infill" — effectively broken.

## Root Cause

**Backend bug** in `ProfilesService.GetAvailableProfilesForPrinterAsync` (slicer-host):
- Line 1945: Calls `ListSystemOrcaProfilesAsync(ct)` which returns ALL system profiles from the slicer DB without any printer/model filtering
- Only Elegoo Centauri profiles were ever seeded into the slicer DB (4 machine + 21 process + 28 filament)
- The OrcaSlicer worker has 2,154 profiles for ALL manufacturers but these haven't been imported for other printers

**Additional backend issue**: `POST /api/slicer/profiles/process/for-machines` returns 503 due to `IPrintersService` not registered in slicer-host DI container.

## Frontend Fix

Changed `ImportOfficialProfilesPage` to:
1. **Use working endpoint**: `GET /slicer/profiles/machine/for-model/{modelId}` (fetches printer-specific machine profiles from the OrcaSlicer worker)
2. **Show actual profiles**: Machine profiles grouped by nozzle diameter with "Imported" badges for already-imported profiles
3. **CTA to Import Wizard**: Primary action navigates to `/profiles/import?modelId={modelId}` (ProfileImportWizardPage) for the full multi-step import flow (machine → filaments → review)
4. **Handle no-model case**: If printer has no linked catalog model, shows a message with "Edit Printer" link

## Backend Issues (Not Fixed — Outside Frontend Scope)

1. `GET /slicer/profiles/available-for-printer/{printerId}` — returns unfiltered Elegoo profiles (needs model-based filtering)
2. `POST /slicer/profiles/process/for-machines` — returns 503 (IPrintersService DI registration missing in slicer-host)
3. `ProfileTaskCheckService` background error: `No service for type 'Farm.Infrastructure.Services.Printers.IPrintersService' has been registered`

## Files Changed

- `src/Web/ReactApp/src/features/slicer/pages/ImportOfficialProfilesPage.tsx` — Rewrote to use model-based machine profile endpoint

## Validation

- ✅ TypeScript: 0 errors
- ✅ Build: Production build succeeds
- ✅ Tests: 1695/1695 pass (0 failures)
- ✅ Lint: ESLint clean
