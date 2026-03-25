## Compact Card PendingReady Backend Verification

- **Date:** 2026-03-25
- **Status:** Investigated

### Decision

Treat the current compact-card PendingReady gap as a UI-path issue unless someone can show that `/api/auto-print/status` is missing `state = PendingReady` for the affected printer.

### Why

- `JobQueueService.AddJobToQueueAsync()` still calls `IAutoPrintService.TransitionToPendingReadyAsync()` after queueing an assigned job, so the first-upload / queued-job path still enters the ready gate.
- `AutoPrintService.TransitionToPendingReadyAsync()` persists `AutoPrintState.PendingReady` and broadcasts `autoprintstatechanged`.
- `AutoPrintController` exposes the same status through both `/api/auto-print/{printerId}/status` and `/api/auto-print/status`.
- `CompactPrinterCard` shows the overlay strictly from the bulk hook path: `useAutoDispatchStatus()` → `/api/auto-print/status` → `isPendingReadyState(status.state)`.

### Evidence

- Focused backend validation passed for the auto-print service + controller regression tests.
- `CompactPrinterCard` does not depend on `AttentionMessage` for the bed-clear overlay; it keys only off `state`.

### Follow-up

Ripley/Kane should inspect the UI data path around `useAutoDispatchStatus()` query hydration/invalidation and the compact-card render flow, because the backend contract currently matches what the banner expects.
