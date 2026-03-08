# Decision: Analytics Frontend Architecture

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-09
**Status:** IMPLEMENTED

## Decision

Created `src/features/analytics/` as a separate feature folder from `src/features/statistics/` to house all 4 new analytics features (dashboard, exports, correlations, predictive alerts).

## Rationale

- **Separation of concerns:** Existing `statistics/` provides simple KPI view. New `analytics/` provides comprehensive business intelligence with correlations, predictions, and exports.
- **Both routes coexist:** `/statistics` (quick overview) and `/analytics` (power user deep-dive) serve different user needs.
- **Reuse over duplication:** Analytics dashboard reuses existing statistics hooks and chart components rather than duplicating them.

## Key Patterns Established

1. **Correlation hooks** use `apiClient.get()` directly (matching `useStatistics.ts` pattern) — no new apiClient class methods needed for GET endpoints.
2. **Export methods** added to `ApiClient` class since they require `responseType: 'blob'` configuration.
3. **Tabs compound component** (`Tabs.List > Tabs.Tab`, `Tabs.Panels > Tabs.Panel`) with string `id` props — not index-based.
4. **Predictive alerts** auto-hide when empty, positioned above KPI cards for visibility.
5. **staleTime strategy:** 300s for correlation analytics (reference data), 60s for alerts (near real-time).

## Dependencies on Lambert

All new API endpoints must be implemented by Lambert before the frontend will render real data:
- `GET /api/correlation-analytics/*` (5 endpoints)
- `GET /api/predictive-analytics/*` (2 endpoints)
- `GET /api/statistics/export/*` (4 endpoints)
