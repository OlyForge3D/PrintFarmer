# Decision: Backend is sole dispatch authority after bed-clear confirmation

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-12
**Status:** IMPLEMENTED

## Context

The bed-clear confirmation flow had a race condition: the backend's `MarkReadyAsync()` triggers `AutoDispatchBackgroundService` to dispatch the job, but the frontend also called `dispatchPrintQueueJob()` — a double-dispatch that caused false error toasts.

## Decision

The backend auto-dispatch background service is the **sole authority** for dispatching after bed-clear confirmation. The frontend confirms bed clear via `POST /autoprint/{id}/ready` and shows appropriate toasts, but never calls the dispatch endpoint directly in this flow.

## Impact

- **Lambert:** The controller comment in `AutoPrintController.cs` (line 40-41) says "The job is NOT automatically dispatched; the client should call the dispatch endpoint" — this is now stale and should be updated to reflect that `NotifyJobQueued` triggers backend dispatch.
- **Frontend:** `BedClearBanner` no longer imports or calls `apiClient.dispatchPrintQueueJob`.
