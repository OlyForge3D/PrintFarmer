---
name: "display-state-overrides"
description: "Teach agents to surface operator-facing UI state from secondary workflow status when the base entity state is insufficient."
domain: "frontend-state"
confidence: "high"
source: "observed"
---

## Context

Use this skill when a frontend shows an entity's primary runtime state (for example `printer.state`) but a secondary workflow state (for example auto-dispatch `PendingReady`) better represents the action the user must take.

## Patterns

### Prefer operator-facing state over raw runtime state

- Keep the raw runtime state for mechanics and transport logic
- Override the displayed label when a workflow-specific state more accurately reflects what the user needs to do
- Centralize that override in a shared formatter/helper instead of scattering one-off checks across cards, tables, and sidebars

### Normalize workflow state checks

- Do not rely on one exact string spelling when checking a workflow state
- Normalize case and separators in one helper so small payload-shape changes do not silently break the UI

### Apply the same override across all views

- Cards
- Tables
- Sidebars or detail panes
- Counters/badges that summarize attention states
- Sorting/filtering that should float attention items to the top

### Suppress stale secondary alerts during optimistic startup transitions

- When one query or mutation optimistically moves the entity into a startup state such as `Starting...`, do not keep rendering a stale error/attention badge from a slower secondary query.
- Prefer the operator-facing startup state until the secondary source catches up, especially for red attention affordances that imply immediate user action.
- Add a card-level regression where the optimistic state and the stale secondary status meet; helper-only tests miss this class of bug.

## Examples

- `src/Web/ReactApp/src/common/utils/printerStateDisplay.ts`
- `src/Web/ReactApp/src/features/printers/components/PrinterTableView.tsx`
- `src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx`
- `src/Web/ReactApp/src/features/printers/components/DetailedPrinterCard.tsx`
- `src/Web/ReactApp/src/common/components/Layout.tsx`
- `src/Web/ReactApp/src/features/printers/components/BedClearBanner.tsx`
- `src/Web/ReactApp/src/test/features/printers/obico-ml-badge.test.tsx`

## Anti-Patterns

- Reading only the base entity state for user-facing labels when a secondary workflow state exists
- Hard-coding repeated `state === 'PendingReady'` checks throughout the UI
- Fixing one view mode while leaving tables, sidebars, or counters inconsistent
- Letting a stale secondary error badge override an optimistic startup state after the user has already dispatched the next step
