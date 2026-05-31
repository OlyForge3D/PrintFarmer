# Decision: Settings Shell uses existing Tabs UI component in controlled mode

**Date:** 2026-05-31
**Author:** Ripley
**Issue:** #357

## Context
Settings consolidation requires a tabbed shell that syncs with URL params. Options were: (a) existing `Tabs` component from `@/common/components/ui`, (b) a new headless tabs implementation, (c) radix-ui or similar.

## Decision
Use the existing `Tabs` component in controlled mode (`activeTab` + `onTabChange`), driven by `useSearchParams`. No new dependency needed.

## Consequences
- Tab state lives in the URL — bookmarkable, shareable.
- `SettingsTabStrip` wraps `Tabs` with filtering logic; tab visibility controlled by search.
- ST-2 (#358) will replace placeholder panels with actual migrated content.
- Old `/settings` page preserved at `/admin/settings-legacy` during migration.
