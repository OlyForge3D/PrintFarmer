# Decision: Standardized Date Range Filters Across Statistics Pages

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-27
**Status:** Implemented

## Context

Three statistics pages had inconsistent date range filtering:
- StatisticsPage: 7d/30d/90d/All time (missing 1 year)
- AnalyticsDashboardPage: 7d/30d/90d/1yr/All time
- CostDashboardPage: No filter at all (always all-time)

Each page duplicated its own button group inline.

## Decision

1. Created shared `TimePeriodFilter` component in `@/common/components/ui/` with standard options: 7 days, 30 days, 90 days, 1 year, All time.
2. All three pages now use this shared component.
3. Cost API hooks (`useCostSummary`, `useCostsByPrinter`, `useCostsByMaterial`) now accept a `days` parameter, passed as query string to the backend.
4. Default selection is 30 days on all pages.

## Impact

- Frontend: 3 pages updated, shared component created, 7 new tests added
- API layer: `apiClient` cost methods now accept `days?` param; query keys changed from static arrays to functions
- Backend: No changes needed — `days` query param was already supported
