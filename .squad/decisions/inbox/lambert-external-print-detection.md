# Decision: External Print Detection for FlashForge + OctoPrint

**Author:** Lambert (Backend Dev)
**Date:** 2026-07-16
**Status:** Implemented

## Context

When a slicer (e.g., OrcaSlicer) sends "Upload and Print" directly to a printer, PrintFarmer detects the "Printing" state via polling but has no corresponding PrintJob record. This causes:
1. `CheckAndSyncJobCompletionAsync` finds no job to complete when print finishes → silent failure
2. UI shows stale "Printing" state indefinitely

## Decision

When a polling service detects a printer transitioning TO "Printing" from a non-printing state AND no active PrintJob exists, create a synthetic external print job record.

## Implementation

- **New property:** `PrintJob.IsExternalPrint` (bool) — marks externally-detected prints
- **New method:** `IPrintJobCompletionService.EnsureExternalPrintJobExistsAsync` — atomic check-and-create
- **Detection location:** Polling services (FlashForge + OctoPrint), triggered on state transitions
- **Guard:** `ExternalJobCreatedForCurrentPrint` flag in `PrinterPollingState` prevents duplicate creation

## Constraints

- External jobs do NOT trigger auto-dispatch (they have `Status=Printing`, not `Queued`)
- External jobs have no `GcodeFileId` (file doesn't exist in PrintFarmer library)
- `ExternalJobId` format: `ext-{printerId}-{timestamp}` for deduplication

## Known Gaps (Out of Scope)

- OctoPrint WebSocket adapter lacks state transition tracking (pre-existing — also affects job completion)
- Moonraker, PrusaLink, SDCP backends have the same external print gap
- No EF migration created for `IsExternalPrint` — SQLite auto-creates column on next startup via `EnsureCreated`
