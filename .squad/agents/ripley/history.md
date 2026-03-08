# Ripley — Frontend Dev History

## Learnings

### EmptyState Component Pattern (2025-07-17)
- Created `EmptyState` at `@/common/components/ui/EmptyState.tsx` with `icon`, `title`, `description`, `action`, `className` props
- Exported from the UI barrel at `@/common/components/ui/index.ts`
- Refactored 3 pages (WebhooksAdminPage, ProjectsPage, JobQueueDashboardPage) from inline empty-state markup to `<EmptyState>`
- The codebase had ~30+ files with ad-hoc empty state patterns — only refactored 3 as requested; more can be migrated incrementally
- Icon wrapper uses `opacity-40` for the muted appearance consistent with existing patterns

### StatisticsPage PageTemplate Wrap (2025-07-17)
- StatisticsPage was the only page bypassing `PageTemplate` — now uses it with `ChartIcon`, subtitle, and period filter buttons as `actions` prop
- PageTemplate's `icon` prop expects a component type (`React.ComponentType`), not a JSX element — pass `ChartIcon` not `<ChartIcon />`
- The period filter buttons moved from inline header to PageTemplate's `actions` slot for consistent layout

### Batch 2 Integration (2026-03-11)
- **EmptyState refactored:** Created Batch 2 decision (PFarm1-y4b) documented in `decisions.md`
- **StatisticsPage PageTemplate:** Formalized as Batch 2 decision (PFarm1-3mn)
- **Pages updated:** All 3 refactored pages now use pf-* design tokens exclusively (no hardcoded gray/slate)
- **Test coverage:** 10 tests added for StatisticsPage PageTemplate validation (structure, formatted values, filter buttons)
- **Validation:** 1,233/1,233 tests pass, full regression guard in place
- **Migration path:** Clear pattern established for ~30 additional empty state migrations in future sprints
