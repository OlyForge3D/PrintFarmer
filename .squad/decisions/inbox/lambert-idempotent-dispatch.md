# Decision: Idempotent Dispatch Endpoint

**Author:** Lambert (Backend Dev)
**Date:** 2026-03-12
**Status:** IMPLEMENTED

## Problem

Race condition between auto-dispatch background service and frontend manual dispatch when confirming bed-clear. Both paths dispatch the same job, and the loser gets a false error.

## Solution

`PrintJobManagementService.DispatchJobAsync` now returns the current job state as success if the job is already `Starting` or `Printing`, instead of throwing. This makes the dispatch endpoint idempotent and safe for concurrent callers.

## Impact

- No breaking changes to existing behavior
- All 162 dispatch/queue-related tests pass
- Frontend no longer sees false "failed to dispatch" errors during auto-dispatch race
