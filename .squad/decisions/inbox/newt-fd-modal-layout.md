# Decision: FailureDetectionStatusModal wide + 2-column layout

**Author:** Newt (Designer — Industrial UI)  
**Date:** 2025-07-22  
**Status:** Proposed

## Context

The spaghetti detection details modal used `size="md"` (max-w-md = 448px). With 6+ content sections stacked vertically — status header, detail tiles, "why this is showing", operator next step, recent incidents, and print session timeline — the modal grew taller than the viewport on large screens, requiring excessive scrolling.

## Decision

1. **Width**: Switched from `size="md"` to `width="max-w-4xl"` (896px). This uses the Modal's `width` prop instead of the preset `size`, giving enough room for a 2-column layout without looking oversized.

2. **Max height**: Tightened from the default `max-h-[90vh]` to `max-h-[85vh]` to add breathing room between the modal edge and the viewport edge.

3. **2-column grid at `lg:` breakpoint**:
   - **Left column** — Context and operator guidance: "Why this is showing", "Operator next step", snapshot link
   - **Right column** — History: Recent incidents, Print session timeline
   - Status header and detail tiles remain full-width above the grid (they're already compact)

4. **Mobile/tablet**: Stays single-column stacked (Tailwind responsive `lg:grid-cols-2` only activates at ≥1024px).

## Rationale

- The context/guidance sections are short text blocks; the history sections are longer lists. Putting them side-by-side on wide screens cuts the vertical height roughly in half.
- 896px (max-w-4xl) is the sweet spot: wide enough for 2 readable columns, narrow enough to not feel like a full-page takeover.
- Snapshot link moved into the left column (from bottom of modal) so it's co-located with operator guidance rather than orphaned at the very end.

## Impact

- Single file changed: `FailureDetectionStatusModal.tsx`
- No test changes needed (no tests asserted on modal size or layout structure)
- All 1615 React tests pass
- ESLint: 0 errors
