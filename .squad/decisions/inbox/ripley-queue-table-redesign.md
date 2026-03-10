# Decision: Queue Table Two-Row Layout

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-12
**Status:** IMPLEMENTED

## Problem

QueueJobsTable had 16 columns in a single flat `<table>` row. It overflowed horizontally even on large displays and didn't feel right for managing print jobs — too much info competing for attention at the same visual level.

## Solution

Redesigned as a two-row-per-job layout using div-based CSS Grid:

- **Row 1 (Primary):** Drag handle, thumbnail, file name, status, printer, copies, priority, actions
- **Row 2 (Secondary):** Project, model, material, filament, est. time, cost, queued date, source — rendered as compact "detail chips" with icons

## Key Design Choices

1. **Div-based instead of `<table>`** — CSS Grid gives precise column sizing without table cell rigidity. The two-row grouping doesn't map cleanly to table semantics anyway.
2. **Detail chips only render when data exists** — no more empty dashes. If a job has no project or cost, that chip simply doesn't appear. Cleaner.
3. **Shortened action labels** ("Cancel" not "Cancel Job", "Abort" not "Abort Print") — saves horizontal space in the actions column.
4. **Secondary row indented 104px** — aligns with the file name column start (40px drag + 56px thumb + 8px gap), creating visual hierarchy.

## Impact

- Tests updated: `[role="listitem"]` replaces `tbody tr` selectors
- `Tractor` icon import removed (non-imported jobs show nothing instead of a tractor icon)
- All existing props and callbacks unchanged — no parent component changes needed
