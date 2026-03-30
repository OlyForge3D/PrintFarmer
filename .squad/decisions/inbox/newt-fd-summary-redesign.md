# Decision: FailureDetectionMonitoringSummary Redesign

**Author:** Newt (Industrial UI Designer)  
**Date:** 2026-06-10  
**Status:** Implemented

## Context

The `FailureDetectionMonitoringSummary` component was taking up excessive visual space on printer cards and looked out of place — it was styled as a standalone monitoring dashboard widget rather than a card section.

## Decision

Redesign the component with two distinct variants:

### Compact Variant (for CompactPrinterCard)
- Single inline row: shield icon + headline text + badge + optional subline
- No stat grid, no "Watching" box
- ~40px height for healthy/standby states
- Operator action text only shown when tone is critical/attention

### Detailed Variant (for DetailedPrinterCard)
- Icon + headline + badge inline
- Summary paragraph below
- Operator action box only when tone is critical/attention
- Still lighter than original — no stat grid or "Watching" box

## Rationale

1. **Card context vs dashboard context**: Cards show at-a-glance status. Operators need tone (color) + headline to know if action is needed. Detailed stats (source, last scan, camera target) belong in a drill-down modal.

2. **Visual weight reduction**: Removed rounded-xl, heavy shadows, gradient backgrounds. Now uses simple rounded-lg with subtle border — matches other card sections.

3. **Information hierarchy**: What operators need on card: "Is this printer OK?" Answer: green badge = OK, red/yellow badge = check it.

## Impact

- Component reduced from 422 lines to 247 lines (41%)
- Visual footprint reduced by ~60-70% on compact cards
- Detailed variant still provides context without dominating card

## Files Changed

- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringSummary.tsx`
- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringSummary.test.tsx`
- `src/Web/ReactApp/src/test/features/printers/obico-ml-badge.test.tsx` (test assertions)
- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringOverlay.test.tsx` (unrelated fix: QueryClientProvider wrapper)
