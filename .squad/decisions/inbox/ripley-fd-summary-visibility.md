# Decision: FailureDetectionMonitoringSummary hidden when printer is at rest

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-27  
**Status:** Implemented

## Context

The `FailureDetectionMonitoringSummary` widget was rendered unconditionally on both compact and detailed printer cards. When a printer is idle/offline/standby, the widget showed "Standing by / Idle" — redundant with the header badge shield icon that already communicates failure-detection state at a glance.

## Assessment: What does the summary show during printing vs at rest?

**During active printing (unique value):**
- Live scan results with last-scanned timestamp
- Failure confidence percentage and detection time
- Operator action directives ("Inspect print", "Check camera")
- Snapshot links for visual review
- Auto-pause status with contextual next steps

**At rest (redundant with header badge):**
- "Standing by" + "Idle" badge — duplicates header shield icon tooltip
- "Off" / "Connecting" — no operational value, header already conveys this
- "Setup needed" — header badge already surfaces misconfigured state

## Decision

Hide `FailureDetectionMonitoringSummary` when `isPrinting` and `isPaused` are both false. The header badge remains the sole failure-detection indicator at rest. The summary widget becomes a print-active operational panel only.

## Impact

- Cleaner cards when printers are at rest (reduced visual noise)
- No loss of information — header badge + tooltip + click-to-modal path still available
- Summary panel surfaces only when operators actually need it (active print monitoring)

## Files Changed

- `CompactPrinterCard.tsx` — wrapped summary in `(isPrinting || isPaused)` guard
- `DetailedPrinterCard.tsx` — same guard
- `FailureDetectionMonitoringSummary.test.tsx` — added card-level visibility contract tests
