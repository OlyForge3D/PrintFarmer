# Decision: Ready → Printing Dispatch Performance Optimization

**Author:** Lambert (Backend Dev)
**Date:** 2026-07-22
**Status:** ✅ IMPLEMENTED

## Summary

Three targeted fixes to reduce the Ready → Printing state transition latency:

### Fix 1: Eliminate Redundant Scoring
- **Before:** `AutoDispatchBackgroundService` scored printers, found the best match, then called `JobDispatchService.DispatchJobAsync` which scored *again* for "audit"
- **After:** New overload `DispatchJobAsync(jobId, printerId, userId, preComputedScore, ct)` accepts pre-computed score, skipping the second 4-query scoring pass
- **Impact:** Eliminated ~50% of DB queries in the dispatch hot path

### Fix 2: Batched DB Saves
- **Before:** 4 serial `SaveChangesAsync` calls (job assignment, dispatch log, mode update, auto-print state reset)
- **After:** 2 calls (assignment+log batched, mode+state batched)
- **Impact:** Eliminated 2 DB round-trips (10-40ms on SQLite/Pi)

### Fix 3: Single Moonraker Upload+Start
- **Before:** Two HTTP calls: `UploadGcodeAsync` then `StartPrintAsync`
- **After:** Single call using Moonraker's `print=true` upload parameter
- **Impact:** Eliminated 1 HTTP round-trip to the printer

## Design Decisions

1. **Kept the no-score overload** for manual dispatch from the UI (where the user explicitly picks a printer and scoring happens on-demand)
2. **Kept `UploadGcodeAsync(baseUrl, fileName, stream)` overload** for upload-only scenarios (no breaking change)
3. **All existing tests updated** to match new mock signatures

## Validation

- Build: 0 errors
- Tests: 1407/1407 API tests passing, all dispatch tests green
- State machine flow unchanged: Ready → dispatch → Starting → Printing
