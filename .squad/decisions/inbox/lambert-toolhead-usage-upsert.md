# Decision: Toolhead Usage Records Use Upsert at Job Completion

**Author:** Lambert  
**Date:** 2026-07-31  
**Status:** Implemented

## Context

The `PrintJobToolheadUsage` table has a unique composite index on `(PrintJobId, ToolheadIndex)`. Dispatch creates snapshot rows (with `SlicerEstimateGrams` + `SpoolmanSpoolId`). Completion must add `FilamentUsageGrams` to those same rows.

## Decision

**Completion always queries for existing rows first.** If snapshot rows exist from dispatch, it updates them in-place (preserving the snapshotted `SpoolmanSpoolId`). If no rows exist (jobs dispatched before the feature), it creates new ones using live toolhead data.

## Rationale

- Avoids `DbUpdateException` from unique index violation
- Preserves the spool assignment recorded at dispatch time, so mid-print spool swaps don't debit the wrong spool
- Backward-compatible: jobs without dispatch snapshots still get usage records

## Applies To

- `PrintJobCompletionService.FetchAndRecordFilamentUsageAsync` — both multi-toolhead and single-spool paths
- Any future code that writes to `PrintJobToolheadUsage` after dispatch
