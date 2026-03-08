# Decision: EmptyState Component as UI Standard

**Author:** Ripley (Frontend Dev)  
**Date:** 2025-07-17  
**Status:** IMPLEMENTED

## Context
The codebase had 30+ files with ad-hoc empty state patterns — inconsistent markup, varying styles, some missing icons or descriptions. This creates visual inconsistency and maintenance burden.

## Decision
Created a shared `EmptyState` component in the UI library (`@/common/components/ui/EmptyState.tsx`) with standardized props: `icon`, `title`, `description`, `action`, `className`.

## Implications for the Team
- **New empty states** should always use `<EmptyState>` — never hand-roll centered "No items" markup
- **Existing pages** can be incrementally migrated (only 3 refactored so far out of ~30+ candidates)
- **Styling** uses `pf-*` tokens only — `text-pf-text-primary` for title, `text-pf-text-secondary` for description, `text-pf-text-tertiary opacity-40` for icon
- **Tests** included: 10 unit tests covering all prop combinations
