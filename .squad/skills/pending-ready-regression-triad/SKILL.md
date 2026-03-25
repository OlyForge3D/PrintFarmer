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
- When a UI surface only has room for a summary badge, expose one computed operator-facing field (for example `AttentionMessage`) on the status DTO instead of making each client reverse-engineer `readyGateChecks`.
- Assert that the summary field explains both the reason and the action: queued work is blocked, the operator must clear the bed, and confirming ready resumes automatic dispatch.
- Cover the UI surface that actually exposes the state: `CompactPrinterCard` should render the `BedClearBanner` overlay when the hook returns `PendingReady`.
- Prefer queue-depth assertions and ready-gate message assertions over checking only the raw enum/state string.

## Examples
- Backend service: `src/tests/Farm.Web.Api.Tests/Services/AutoPrint/AutoPrintServiceTests.cs`
- Backend API: `src/tests/Farm.Web.Api.Tests/Controllers/AutoPrintPendingReadyTests.cs`
- Frontend card overlay: `src/Web/ReactApp/src/test/features/printers/obico-ml-badge.test.tsx`
- Shared state normalization: `src/Web/ReactApp/src/common/utils/printerStateDisplay.ts`

## Anti-Patterns
- Testing only `MarkPreClearAsync` and assuming PendingReady coverage exists.
- Verifying only the single-printer endpoint when the page relies on the bulk `/api/auto-print/status` payload.
- Asserting only that the banner component works in isolation without proving the printer card actually mounts it.
