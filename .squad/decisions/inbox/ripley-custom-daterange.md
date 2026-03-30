# Decision: Custom Date Range Picker for TimePeriodFilter

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-27
**Status:** Implemented

## Context

Lambert shipped backend `startDate`/`endDate` query param support on all statistics endpoints. Frontend only had preset buttons (7d/30d/90d/1yr/All Time).

## Decision

Introduced `TimePeriodFilterValue` discriminated union type:
```typescript
type TimePeriodFilterValue =
  | { type: 'preset'; days: number | undefined }
  | { type: 'custom'; startDate: string; endDate: string };
```

- Added "Custom" toggle button to `TimePeriodFilter`; when active, shows inline date inputs with min/max constraints
- Pages manage `TimePeriodFilterValue` state and derive `days`/`startDate`/`endDate` for hooks
- Updated all cost API methods and hooks to accept optional `startDate/endDate` alongside `days`
- Updated `useStatistics` hooks with same pattern using shared `buildStatsParams()` helper
- All three dashboard pages (Cost, Statistics, Analytics) updated

## Trade-offs

- **Breaking change** to `TimePeriodFilterProps` — accepted because only 3 consumers exist and all needed updating
- Custom mode uses fully controlled inputs (no intermediate state) — clean but means invalid dates silently reject
- `ExportMenu` still takes `days` only — acceptable since exports can use the preset-derived value

## Files Changed

- `timePeriodOptions.ts`, `TimePeriodFilter.tsx`, `index.ts` (UI library)
- `api.ts` (cost methods), `useApi.ts` (cost hooks + query keys)
- `useStatistics.ts` (statistics hooks)
- `CostDashboardPage.tsx`, `StatisticsPage.tsx`, `AnalyticsDashboardPage.tsx`
- `TimePeriodFilter.test.tsx` (new), `CostDashboardPage.test.tsx` (updated)
