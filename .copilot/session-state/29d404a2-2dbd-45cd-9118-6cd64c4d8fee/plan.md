# Plan: Auto-Print / Auto-Dispatch Separation

## Problem

The current codebase conflates two distinct features under the "Auto-Print" label:
1. **Ready Gate** (bed clear confirmation between prints) — the current `AutoPrintState` workflow
2. **Auto-Dispatch** (system automatically sends queued jobs to available printers)

Jeff's directives:
- **Auto-Print** = future hardware feature (automatic bed clearing). Per-printer setting in Add/Edit modal. NOT currently implemented.
- **Auto-Dispatch** = system dispatches jobs to printers. System-level toggle + per-printer opt-in icon on cards.
- Remove "Auto-Print" toggle from printer cards → replace with auto-dispatch icon
- Remove "Auto-Print" toggle from queue dashboard → replace with "Auto-Dispatch" toggle
- **Revert unassigned jobs** — if no printer matches, DON'T queue. Return error.
- **No idle delay for upload-and-print** — dispatch immediately when printer is available
- **Ready gate stays** — between consecutive prints, user confirms bed is clear

## Approach

**Phase 1**: Frontend UI relabeling (what users see)
**Phase 2**: Backend behavioral fixes (dispatch trigger, revert unassigned jobs, skip idle threshold)
**Phase 3**: Backend renames (future, not this session)

## Todos

### Phase 1: Frontend UI Changes

- [ ] `ui-queue-auto-dispatch-toggle` — Replace "Auto-Print" toggle on queue dashboard with "Auto-Dispatch" toggle. Same underlying API, just relabeled.
- [ ] `ui-printer-card-dispatch-icon` — Replace label+toggle for Auto-Print on printer cards with a compact icon toggle for auto-dispatch opt-in (e.g., lightning bolt or dispatch icon).
- [ ] `ui-remove-autoprint-labels` — Audit all frontend text that says "Auto-Print" and change to "Auto-Dispatch" where it refers to the dispatch system. Keep "Bed Clear" / "Ready" terminology for the gate workflow.

### Phase 2: Backend Behavioral Fixes

- [ ] `fix-dispatch-trigger-on-queue` — Inject `IAutoDispatchTrigger` into `JobQueueService`. Fire trigger after `AddJobToQueueAsync` creates a job assigned to an idle printer.
- [ ] `fix-dispatch-query-assigned` — Update `AutoDispatchBackgroundService.ExecuteDispatchCycleAsync` to also find jobs that ARE assigned to the triggering printer (not just unassigned jobs).
- [ ] `revert-unassigned-jobs` — Revert the unassigned job creation. `AddJobToQueueAsync` should return null when no printer matches. `OctoPrintCompatController` should return an error response (not 200).
- [ ] `skip-idle-threshold-upload` — When dispatch is triggered by a new job queue (not by print completion), skip the `IdleThresholdSeconds` delay.

### Phase 3: Future (not this session)
- [ ] Rename `AutoPrintEnabled` → `AutoDispatchOptIn` on Printer entity + migration
- [ ] Rename `AutoPrintController` → `ReadyGateController`
- [ ] Rename `IAutoPrintService` → `IReadyGateService`
- [ ] Add `AutoPrintCapable` boolean on Printer for future hardware support
- [ ] Move Auto-Print setting to Add/Edit printer modal
