# Decision: Consolidate profile import to single wizard flow

**Date:** 2026-07-25
**Author:** Ripley (Frontend Dev)
**Status:** Implemented

## Context

Two separate profile import paths existed in the UI:
1. `/profiles/import` — Profile Import Wizard (worker-backed, working correctly)
2. `/slicer/import-official` — Import Official Profiles page (DB-backed, broken)
3. `/admin/slicer-profiles` — Slicer Profiles admin page (browse/manage)

Users reported the "Import Official Profiles" flow from the Slice onboarding was broken.
Dallas decided the working Profile Import Wizard is the surviving flow.

## Changes

- **NewSliceJobPage onboarding**: "Import Official Profiles" button now navigates to `/profiles/import` instead of `/slicer/import-official`. "Browse Profiles" button removed (it led to the admin page being retired).
- **Navigation**: "Slicer Profiles" admin nav item removed from sidebar.
- **Routing**: `/slicer/import-official` and `/admin/slicer-profiles` now redirect to `/profiles/import`. Old page components (`ImportOfficialProfilesPage`, `SlicerProfilesPage`) are no longer lazy-loaded in App.tsx.
- **Tests**: Updated `NewSliceJobPageOnboarding.test.tsx` to expect `/profiles/import` nav target and removed the "Browse Profiles" button test.

## What was NOT changed

- Backend APIs remain untouched (as directed).
- The old page component files (`ImportOfficialProfilesPage.tsx`, `SlicerProfilesPage.tsx`) still exist on disk for reference. They are dead code — no route loads them.
- The `pages/SlicerProfilesPage.tsx` (root pages dir copy) also remains on disk as dead code.

## Impact

- Users have a single, working import path from any entry point.
- Old bookmarks to `/slicer/import-official` or `/admin/slicer-profiles` redirect cleanly.
- No nav clutter from a duplicate admin page.
