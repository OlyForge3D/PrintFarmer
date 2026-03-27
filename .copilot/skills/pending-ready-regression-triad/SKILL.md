---
name: "pending-ready-regression-triad"
description: "How to test PendingReady visibility across service logic, status APIs, and printer-card UI"
domain: "testing"
confidence: "high"
source: "earned"
---

## Context
Use this when a printer should appear in the PendingReady / awaiting-bed-clear state but does not show up correctly in the dashboard, printer cards, or nav attention badges.

## Patterns
- Cover the service transition first: `TransitionToPendingReadyAsync` should set `AutoPrintState.PendingReady`, keep `BedPreConfirmed` false, and broadcast `autoprintstatechanged` with the waiting-for-operator gate.
- Cover both API shapes: verify `GET /api/auto-print/{printerId}/status` and `GET /api/auto-print/status` return `state = PendingReady`, queue depth, and a failed `Bed Clear Confirmed` ready gate.
- When backend emits `autoprintstatechanged`, wire that event into the same React Query cache used by cards/tables (`['auto-dispatch', 'all-statuses']`) so PendingReady UI does not wait for the next poll interval.
- For the compact-card path specifically, trace the exact contract: `useAutoDispatchStatus()` reads the bulk `/api/auto-print/status` payload, and `CompactPrinterCard` mounts `BedClearBanner` when `requiresBedClearConfirmation(status)` is true. The overlay should appear both for an explicit `PendingReady` state and for a stale summary row whose failed `Bed Clear Confirmed` gate still says the operator must clear the bed.
- When a UI surface only has room for a summary badge, expose one computed operator-facing field (for example `AttentionMessage`) on the status DTO instead of making each client reverse-engineer `readyGateChecks`.
- Assert that the summary field explains both the reason and the action: queued work is blocked, the operator must clear the bed, and confirming ready resumes automatic dispatch.
- Cover the UI surface that actually exposes the state: `CompactPrinterCard` should render the `BedClearBanner` overlay when the hook returns `PendingReady`.
- Add one characterization test for the initial bulk fetch path, not just SignalR updates; real regressions often happen on first page load before any live event arrives.
- Prefer queue-depth assertions and ready-gate message assertions over checking only the raw enum/state string.
- Frontend surfaces should treat a failed `readyGateChecks["Bed Clear Confirmed"]` gate as equivalent to PendingReady when deciding whether to show the bed-clear banner or Pending Ready label. Do not trust a single summary `state` field if the detailed gate says operator action is still required.
- If the gate is red (`passed === false`) and queued work still exists, keep the PendingReady/banner fallback even when the gate message is blank or omitted. Only the explicit `"No confirmation needed yet"` copy should suppress the operator alert.

## Examples
- Backend service: `src/tests/Farm.Web.Api.Tests/Services/AutoPrint/AutoPrintServiceTests.cs`
- Backend API: `src/tests/Farm.Web.Api.Tests/Controllers/AutoPrintPendingReadyTests.cs`
- Frontend card overlay: `src/Web/ReactApp/src/test/features/printers/obico-ml-badge.test.tsx`
- Frontend live update regression: `src/Web/ReactApp/src/test/features/printers/compact-printer-pendingready-live.test.tsx`
- Shared state normalization: `src/Web/ReactApp/src/common/utils/printerStateDisplay.ts`
- SignalR cache bridge: `src/Web/ReactApp/src/features/printers/hooks/useAutoDispatch.ts`

## Anti-Patterns
- Testing only `MarkPreClearAsync` and assuming PendingReady coverage exists.
- Verifying only the single-printer endpoint when the page relies on the bulk `/api/auto-print/status` payload.
- Leaving the frontend on polling-only status refresh after the backend already broadcasts `autoprintstatechanged`.
- Assuming a SignalR-only regression is enough; the first-render bulk snapshot can regress independently of live updates.
- Requiring descriptive gate copy before showing PendingReady when the gate is already red and queue depth is non-zero.
- Asserting only that the banner component works in isolation without proving the printer card actually mounts it.
