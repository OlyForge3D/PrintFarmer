---
name: "Unified Tabbed Hub"
description: "Collapse multiple related pages into one deep-linkable tabbed hub while reusing existing page bodies"
domain: "frontend-routing"
confidence: "medium"
source: "earned from session 2026-06-01T15:18:38-07:00 for issue #437"
---

## Context

Use this pattern when several adjacent routes represent different lenses on the same operational area and the product direction is to expose a single destination with tabs.

## Pattern

1. Keep the existing route pages as standalone wrappers with their `PageTemplate` intact.
2. Extract each page's inner content into exported body components.
3. Create a hub page that owns the shared header, top-level filters, KPI strip, and controlled tab state.
4. Drive the selected lens from `useSearchParams` so URLs can deep-link to a specific tab.
5. Redirect legacy routes into the new hub with explicit query params.

## PrintFarmer Example

- Hub: `src/Web/ReactApp/src/features/analytics/pages/AnalyticsHubPage.tsx`
- Reused bodies:
  - `StatisticsDashboardContent`
  - `CostDashboardContent`
  - `AnalyticsDashboardContent`
- Legacy redirects:
  - `/statistics` → `/analytics?lens=production`
  - `/statistics/costs` → `/analytics?lens=cost`

## Accessibility Notes

- Use the shared `Tabs` component in controlled mode for URL-backed tab state.
- Ensure the tabs expose `tablist` / `tab` / `tabpanel` semantics.
- Add ArrowLeft/ArrowRight/Home/End support with roving `tabIndex` in the shared tabs primitive instead of patching one page locally.

## Verification

- Build: `cd src/Web/ReactApp && npm run build`
- Lint: `cd src/Web/ReactApp && npm run lint`
- Focused tests should cover:
  - URL deep-link selection
  - keyboard tab switching
  - legacy route redirects
  - nav pointing to the new single destination
