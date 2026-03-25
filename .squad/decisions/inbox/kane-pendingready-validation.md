## Kane — PendingReady Validation Decision

- **Date:** 2026-03-25
- **Status:** Proposed from test validation

### Decision

Treat `autoprintstatechanged` as the authoritative live update for PendingReady / bed-clear UI, and immediately sync that event into the React Query auto-dispatch caches used by compact cards, tables, and nav attention counts.

### Why

Backend coverage already proved the PendingReady transition and SignalR broadcast existed, but the frontend only refreshed `/api/auto-print/status` on a 10-second poll. That left a real gap where the compact card could stay on `Idle` long enough for operators to conclude the banner/state change never arrived.

### Evidence

- Backend service test: `src/tests/Farm.Web.Api.Tests/Services/AutoPrint/AutoPrintServiceTests.cs`
- Backend API test: `src/tests/Farm.Web.Api.Tests/Controllers/AutoPrintPendingReadyTests.cs`
- Frontend live regression: `src/Web/ReactApp/src/test/features/printers/compact-printer-pendingready-live.test.tsx`

### Impact

- Compact printer cards update to `Pending Ready` immediately after the workflow transition.
- `BedClearBanner` mounts without waiting for the next polling interval.
- Shared auto-dispatch caches stay aligned across compact cards and any other surface reading the same query keys.
