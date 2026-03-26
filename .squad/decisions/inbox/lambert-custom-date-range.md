# Decision: Custom Date Range API Contract

**Author:** Lambert (Backend Dev)
**Date:** 2026-07-14
**Status:** Implemented

## Context

Statistics endpoints previously only supported `?days=N` for time filtering. Operators need arbitrary date ranges for reporting and cost analysis.

## Decision

All 9 statistics endpoints now accept optional `startDate` and `endDate` query parameters (ISO 8601 format). Priority order:

1. `startDate`/`endDate` (custom range) — takes precedence
2. `days` — calculated from UTC now (existing behavior)
3. No params — endpoint default (all-time or 30 days depending on endpoint)

## Constraints

- `startDate` must be before `endDate` (400 if violated)
- Max range: 730 days / 2 years (400 if exceeded)
- Cost queries filter on `ActualEndTime`; non-cost queries filter on `QueuedAt`

## Impact

- **Frontend**: Can now build custom date range pickers for analytics dashboards
- **API consumers**: Fully backward-compatible; existing `?days=N` calls unchanged
- **Export endpoints**: Not yet updated (use `ReportRequest.Days` internally)
