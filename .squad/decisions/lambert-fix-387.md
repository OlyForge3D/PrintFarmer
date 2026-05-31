# Lambert Fix: PR #387 PowerMonitor UI → Real Endpoints

**Date:** 2025-06-01  
**Author:** Lambert (backend filling in per lockout rule — Ripley locked out of #348)  
**PR:** #387  
**Related:** #348 (PowerMonitor UI), PR #404 / branch `squad/386-power-monitor-crud` (backend)

## Decision

Wire the PowerMonitor management UI to the real `AdminPowerMonitorsController` endpoints shipped in PR #404 (`squad/386-power-monitor-crud`).

## Problem

PR #387 shipped the PowerMonitor settings page with TODO comments explicitly acknowledging that `/api/admin/power-monitors` didn't exist yet. The hooks contained a large comment block warning that "these hooks call best-guess endpoints that will be implemented in a follow-up issue." The fallback rate save handler was a stub that printed a success toast without calling any API.

## Changes Applied

### `usePowerMonitors.ts`
- Removed TODO/placeholder comment block. Endpoint paths were already correct (`/admin/power-monitors`, `/admin/power-monitors/${id}`, `/admin/power-monitors/test`) — no path changes needed.

### `PowerMonitorSettingsPage.tsx`
- Added `useEffect` to load the farm-wide electricity rate from `apiClient.getCostTrackingSettings()` on mount.
- `handleSaveFallbackRate` now calls `apiClient.updateCostTrackingSettings({ ...current, electricityRatePerKwh: rate })` instead of the placeholder stub.
- Removed "The backend endpoint may not be available yet" from the error message.

### New test: `src/test/features/power-monitors/usePowerMonitors.test.ts`
- Covers all five hooks: list, create, update, delete, test-connection.
- Verifies correct HTTP method and path for each mutation.
- Tests `success: false` path on test-connection without throwing.

## Backend Contract (from PR #404)

| Method | Path | Hook |
|---|---|---|
| GET | `/api/admin/power-monitors` | `usePowerMonitors` |
| POST | `/api/admin/power-monitors` | `useCreatePowerMonitor` |
| PUT | `/api/admin/power-monitors/{id}` | `useUpdatePowerMonitor` |
| DELETE | `/api/admin/power-monitors/{id}` | `useDeletePowerMonitor` |
| POST | `/api/admin/power-monitors/test` | `useTestPowerMonitorConnection` |

Note: The test endpoint accepts `{ provider, deviceAddress }` in the request body (not `{id}` in the path as initially specced — the shipped controller uses a body-based approach without requiring a saved record ID).

## Rationale

The UI was complete and well-structured but intentionally left with stubs due to the backend blocker. Now that PR #404 is merged into the branch, the stubs are replaced with live calls. No structural changes to component architecture were made.
