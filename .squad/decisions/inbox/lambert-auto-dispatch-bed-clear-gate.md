# Decision: Auto-Dispatch Respects Auto-Print Bed-Clear Gate

**Author:** Lambert (Backend Dev)
**Date:** 2026-07-12
**Status:** Implemented

## Context

The auto-dispatch and auto-print pipelines were operating independently. Auto-dispatch could bypass the bed-clear confirmation gate when dispatching to printers with auto-print enabled.

## Decision

Auto-dispatch now checks `Printer.AutoPrintState` before dispatching to auto-print-enabled printers:
- If `AutoPrintEnabled=true` and `AutoPrintState != Ready`, auto-dispatch skips the printer (waits for operator confirmation)
- After operator confirms bed-clear (`MarkReadyAsync`), auto-print triggers auto-dispatch via `NotifyJobQueued`
- After successful dispatch, `AutoPrintState` resets to `None` for the next cycle

## Impact

- **Ripley (Frontend):** The `autoprintstatechanged` SignalR event now fires when a job is queued to an idle auto-print printer (not just after print completion). The UI bed-clear prompt should appear immediately on first upload.
- **Kane (QA):** New test scenarios needed: (1) first upload triggers PendingReady, (2) auto-dispatch skips printers in PendingReady state, (3) MarkReady triggers dispatch
