---
name: "Unified Files Route"
description: "Collapse separate file-library tabs into one route-backed browser with query-param filters and legacy link normalization."
domain: "frontend-routing"
confidence: "medium"
source: "earned from session 2026-06-03T10:36:27.015-07:00 for unified files page"
---

## Context

Use this pattern when multiple file-library pages should become a single destination, but the backend still exposes separate endpoints for each source.

## Patterns

- Keep the canonical route at `/files`.
- Drive the active file lens from `?type=` values like `all`, `models`, `gcode`, and `other`.
- Rewrite legacy child routes such as `/files/gcode` and `/files/3d-models` to the canonical route with `replace` navigation.
- Merge source-specific queries into one client-sorted list when no unified API exists yet.
- Prefix mixed-source selection IDs (for example `model:<id>` and `gcode:<id>`) so bulk actions can recover the correct backend target.
- Keep non-list workflows like harvest as page actions or inline status surfaces instead of separate tabs.

## Examples

- `src/Web/ReactApp/src/features/files/pages/FilesPage.tsx`
- `src/Web/ReactApp/src/features/queue/components/QueueJobsTable.tsx`

## Anti-Patterns

- Reintroducing `/files/*` tabs for file-type lenses.
- Sharing raw IDs across mixed model and G-code selection state.
- Removing a tab without giving its workflow a new entry point on the unified page.
