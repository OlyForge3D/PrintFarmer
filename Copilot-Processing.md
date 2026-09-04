# Copilot Processing — Slice Job Failure Diagnostics

## User Request

Improve the Workers -> Jobs admin page (SliceJobsPanel) so failed slice jobs
expose actionable diagnostics instead of only "Slicing failed." Coordinate
conceptually with the backend's existing safe `failureReason`/`failureHint`/
`errorDetail` rules: do not expose sensitive raw details to unauthorized
users, but ensure farm admins can see the useful server diagnostic when the
API provides it. Preserve the compact table/expanded-row UX and
accessibility conventions. Add/update focused React tests. Run the smallest
relevant test/lint/build commands, update this tracking file, commit with
the required Co-authored-by trailer, and report files/behavior/tests.

## Plan

1. Inspect `SliceJobsPanel.tsx`, `sliceJobService.ts` DTO, failure notice
   components, backend `errorDetail` visibility contract, and existing
   focused tests.
2. Render the admin-only `errorDetail` diagnostic (already fetched by the
   frontend type but never displayed) gated behind `hasRole('farm_admin')`,
   alongside the existing client-safe `failureHint`.
3. Ensure `useSliceJobsRealtime` clears stale `errorDetail` the same way it
   already clears `failureReason`/`failureHint` once a job is no longer
   `Failed`.
4. Update/add focused React tests for admin vs non-admin visibility and for
   the realtime staleness-clearing behavior.
5. Validate with targeted `test:run`, `lint`, and `build`.

## Tasks

- [x] Read `SliceJobsPanel.tsx`, `sliceJobService.ts`, `useSliceJobsRealtime.ts`,
      backend `SliceJobController.cs` / `SliceJobErrorDetailVisibilityTests.cs`.
- [x] Add `useAuth` import and gate `errorDetail` rendering behind
      `hasRole('farm_admin')` in `SliceFailureNotice`.
- [x] Update `JobDetailPanel` gating condition to show the notice when either
      `failureHint` or `errorDetail` is present.
- [x] Clear stale `errorDetail` in `useSliceJobsRealtime.applyEventToJob`.
- [x] Add `useAuth` mocks to existing tests that render `SliceJobsPanel`
      (`SliceJobsPanel.keyboard.test.tsx`, `SliceJobsLayoutDegradationNotice.test.tsx`,
      `SliceJobsPreviewButton.test.tsx`, `SliceJobsPanel.completionEvent.test.tsx`).
- [x] Add 3 new cases to `SliceJobsFailureNotice.test.tsx` (hide from
      non-admin, show to admin, show to admin even without failureHint).
- [x] Add a new case to `useSliceJobsRealtime.test.ts` covering `errorDetail`
      staleness-clearing.
- [x] Run targeted `npm run test:run` for the touched slicer test files — all
      6 files / 30 tests pass.
- [x] Run `npm run lint` — clean.
- [x] Run `npm run build` — succeeds.
- [x] Commit changes with Co-authored-by trailer.

## Summary

- **Files changed**:
  - `src/Web/ReactApp/src/features/slicer/components/SliceJobsPanel.tsx`
  - `src/Web/ReactApp/src/features/slicer/hooks/useSliceJobsRealtime.ts`
  - `src/Web/ReactApp/src/test/features/slicer/components/SliceJobsFailureNotice.test.tsx`
  - `src/Web/ReactApp/src/test/features/slicer/components/SliceJobsPanel.keyboard.test.tsx`
  - `src/Web/ReactApp/src/test/features/slicer/components/SliceJobsLayoutDegradationNotice.test.tsx`
  - `src/Web/ReactApp/src/test/features/slicer/components/SliceJobsPreviewButton.test.tsx`
  - `src/Web/ReactApp/src/test/features/slicer/components/SliceJobsPanel.completionEvent.test.tsx`
  - `src/Web/ReactApp/src/test/features/slicer/hooks/useSliceJobsRealtime.test.ts`

- **Behavior**: The failed-job detail panel now renders, in addition to the
  existing client-safe `failureHint` warning, a separate error-styled
  "Diagnostic:" block containing the real worker-side `errorDetail` — but
  only for users with the `farm_admin` role. Non-admins never see it (the
  backend already omits it for them; the frontend adds a defense-in-depth
  gate). The failure notice now also appears when a job has `errorDetail`
  but no classified `failureHint`. Stale `errorDetail` from a previous
  attempt is cleared once a retried job's realtime status moves away from
  `Failed`, mirroring the existing `failureReason`/`failureHint` clearing.

- **Tests**: 6 targeted test files / 30 tests pass (`npm run test:run`),
  `npm run lint` clean, `npm run build` succeeds.
