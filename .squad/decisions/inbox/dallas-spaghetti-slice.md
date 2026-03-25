# Decision: Spaghetti Detection — Phase 1 Delivery Slice

**Author:** Dallas  
**Date:** 2025-07-14  
**Status:** Proposed

## Context

Jeff asked for "backend and UI for spaghetti detection." We already have substantial infrastructure:

### What Exists Today (Working)
- **Backend monitoring loop** — `PrintFailureMonitorService` polls cameras on a configurable interval, sends snapshots to Obico ML, broadcasts `FailureDetected` via SignalR
- **Obico ML integration** — `ObicoFailureDetectionService` submits images, parses confidence scores, compares against threshold
- **Obico server CRUD** — Full API at `/api/obico-servers`, UI in Settings → Monitoring → `ObicoServersSection`
- **Settings** — `ObicoSettings` with enable toggle, API URL, confidence threshold, scan interval, auto-pause flag — all rendered in the dynamic settings UI
- **SignalR plumbing** — `FailureDetected` event wired end-to-end: backend broadcasts `FailureDetectionDto`, frontend `printer-signalr.ts` receives it, `App.tsx` shows a toast
- **Printer card badges** — Compact and Detailed cards show "ML" badge when `obicoEnabled && isPrinting`
- **Manual analysis endpoint** — `POST /api/failure-detection/analyze/{printerId}`

### What's Missing / Broken
1. **History endpoint is 501** — `GET /api/failure-detection/history` returns "not implemented." No persistence layer for detection events.
2. **Auto-pause is a no-op** — `PrintFailureMonitorService.HandleFailureDetectedAsync` logs a warning but never calls the backend client's pause method. `IBackendClientFactory` exists, pause methods exist on all backends (Moonraker, PrusaLink, OctoPrint, SDCP), but the monitor doesn't inject or use it.
3. **No dedicated spaghetti detection page** — Detection events are fire-and-forget toasts. No place to see current monitoring status, recent detections, or take action.
4. **Status endpoint is anemic** — `GET /api/failure-detection/status` returns a static message, not actual per-printer monitoring state.
5. **FailureDetectionDto lacks snapshot URL** — When a failure is detected, the camera snapshot that triggered it isn't preserved in the DTO or broadcast.

## Phase 1 Scope — "See It, React to It"

The goal is: a user can see that spaghetti detection is happening, see when it fires, and have it actually pause their print. No history persistence yet — that's Phase 2.

### In Scope

#### Lambert (Backend)

1. **Wire auto-pause through `IBackendClientFactory`**
   - Inject `IBackendClientFactory` into `PrintFailureMonitorService`
   - On failure detection, resolve the printer's backend client and call its pause method
   - Set `failureEvent.AutoPaused = true` on success
   - Graceful fallback: if pause fails, log the error and broadcast with `AutoPaused = false`
   - The backend clients already support pause: `PausePrintAsync` / `PauseJobAsync` / etc.

2. **Enrich `FailureDetectionDto` with snapshot URL**
   - Add `string? SnapshotUrl` to `FailureDetectionDto`
   - Populate it in `HandleFailureDetectedAsync` from the camera's `SnapshotUrl`
   - Frontend can use this to show the "what triggered it" image

3. **Improve status endpoint to return real data**
   - `GET /api/failure-detection/status` should return:
     ```json
     {
       "enabled": true,
       "monitoredPrinterCount": 3,
       "activePrinterCount": 1,
       "scanIntervalSeconds": 30,
       "confidenceThreshold": 0.7,
       "autoPauseEnabled": true,
       "lastScanAt": "2025-07-14T10:00:00Z"
     }
     ```
   - This requires `PrintFailureMonitorService` to track and expose `LastScanAt` and counts. Add a simple `IFailureMonitorStatus` interface the controller can read.

#### Ripley (Frontend)

4. **Add `snapshotUrl` to `FailureDetectionEvent` type**
   - Update `src/types/api.ts` — add `snapshotUrl?: string` to `FailureDetectionEvent`

5. **Improve the toast notification**
   - Show the confidence as a percentage (already done)
   - Add a "View" action button on the toast that opens the snapshot URL in a new tab (or shows it in a lightweight modal)
   - Differentiate auto-paused vs. not-paused in the toast styling (red vs. amber)

6. **Add a failure detection status indicator to the printer card**
   - When a `FailureDetected` event arrives for a specific printer, show a warning badge/icon on that printer's card (both Compact and Detailed variants)
   - This should be transient — clear after a reasonable timeout (e.g., 60s) or when the user dismisses it
   - The existing "ML" badge shows monitoring is active; this new badge shows "failure detected"

7. **Expose monitoring status in the Settings → Monitoring section**
   - Query `GET /api/failure-detection/status` and display the real-time monitoring status (monitored printers, last scan, etc.) above the Obico servers list
   - Keep it simple: a status card with key metrics, not a full dashboard

### Deferred to Phase 2

- **Event persistence** — `FailureDetectionEvent` entity, EF migration, repository, history API. This is a full schema addition across both DB providers. Not worth rushing in Phase 1.
- **Detection history page** — Requires persistence. Deferred.
- **Per-printer detection settings** — Currently enable/disable is global. Per-printer granularity is nice-to-have.
- **Confidence trend charts** — Requires history data.
- **Notification channels** (email, Telegram, etc.) — Out of scope.
- **Detection event acknowledgment/dismiss workflow** — Phase 2 with persistence.

## API Contract Changes

### Modified: `GET /api/failure-detection/status`

**Response (200):**
```json
{
  "enabled": boolean,
  "monitoredPrinterCount": number,
  "activePrinterCount": number,
  "scanIntervalSeconds": number,
  "confidenceThreshold": number,
  "autoPauseEnabled": boolean,
  "lastScanAt": string | null
}
```

### Modified: `FailureDetectionDto` (SignalR broadcast)

**Added field:**
- `snapshotUrl` (`string?`) — URL of the camera snapshot that triggered detection

### Unchanged
- `POST /api/failure-detection/analyze/{printerId}` — stays as-is
- `GET /api/failure-detection/history` — stays 501 until Phase 2
- All Obico server CRUD endpoints — no changes

## TypeScript Type Changes

```typescript
// Updated FailureDetectionEvent
export interface FailureDetectionEvent {
  printerId: string;
  printerName: string;
  jobId?: string;
  confidence: number;
  detectedAt: string;
  autoPaused: boolean;
  snapshotUrl?: string;  // NEW
}

// NEW: status endpoint response
export interface FailureDetectionStatus {
  enabled: boolean;
  monitoredPrinterCount: number;
  activePrinterCount: number;
  scanIntervalSeconds: number;
  confidenceThreshold: number;
  autoPauseEnabled: boolean;
  lastScanAt: string | null;
}
```

## Execution Order

1. Lambert: Wire auto-pause (#1) — this is the highest-value change
2. Lambert: Enrich DTO (#2) + status endpoint (#3) — can be one PR
3. Ripley: Type updates (#4) + toast improvements (#5) — parallel with Lambert
4. Ripley: Printer card warning badge (#6) + settings status card (#7) — after Lambert's status endpoint lands

Items 1-2 (Lambert) and 3-4 (Ripley) can start in parallel. Item 5-6 (Ripley) depends on Lambert's status endpoint.

## Key Files

**Backend:**
- `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs` — main changes
- `src/infra/Dtos/FailureDetectionDto.cs` — add SnapshotUrl
- `src/api/Controllers/FailureDetectionController.cs` — status endpoint
- `src/infra/Services/Printers/IBackendClientFactory.cs` — already exists, inject into monitor

**Frontend:**
- `src/Web/ReactApp/src/types/api.ts` — type additions
- `src/Web/ReactApp/src/App.tsx` — toast improvements
- `src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx` — warning badge
- `src/Web/ReactApp/src/features/printers/components/DetailedPrinterCard.tsx` — warning badge
- `src/Web/ReactApp/src/features/admin/pages/SettingsPage.tsx` — monitoring status card
- `src/Web/ReactApp/src/services/api.ts` — new status fetch method
