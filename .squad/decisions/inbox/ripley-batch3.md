# Ripley Batch 3 Decisions

## Decision: Printer Card Decomposition Architecture

**Date:** 2026-03-07  
**Beads:** PFarm1-qhu, PFarm1-4tc  
**Status:** ✅ COMPLETED

### Context
DetailedPrinterCard was a 1037-line monolith handling status, temperatures, movement, filament control, camera feeds, file browsing, history, spool management, progress display, and print actions. CollapsedPrinterCard and DetailedPrinterCard both computed status indicator colors independently using duplicate logic.

### Decision
1. **Extract shared status color utility** (`statusColors.ts`)
   - Created `getStatusIndicatorColor()` returning pf-* token classes
   - Maps all printer states: offline → `bg-pf-disabled`, printing → `bg-pf-success-bg animate-pulse`, paused → `bg-pf-warning`, error/shutdown → `bg-pf-error`, idle → `bg-pf-accent-bg`
   - Refactored both CollapsedPrinterCard and DetailedPrinterCard to use shared utility

2. **Decompose DetailedPrinterCard into 5 section components**
   - `PrinterStatusHeader` (52 lines) — name, status dot, online/offline badge
   - `TemperatureControlSection` (151 lines) — hotend/bed temps, presets, set-temp controls
   - `MovementControlSection` (347 lines) — XYZ movement, homing, extrusion, manual inputs
   - `FilamentControlSection` (54 lines) — load/unload/change filament macros
   - `PrinterActionBar` (62 lines) — pause/resume/cancel/emergency stop

3. **Parent-child prop passing pattern**
   - DetailedPrinterCard manages state and API calls
   - Section components receive props — no duplicate API calls or state
   - Capability checks performed in parent, passed down as boolean flags

### Implementation
- All new components use TypeScript interfaces for props
- All components use pf-* design tokens exclusively
- Each section component < 350 lines (most < 200 lines)
- DetailedPrinterCard reduced from 1037 to 701 lines
- Pure refactor — identical functionality, no behavior changes

### Validation
- ✅ 1,293/1,293 tests passing
- ✅ 0 lint errors
- ✅ All existing functionality preserved
- ✅ CollapsedPrinterCard and DetailedPrinterCard now use shared status utility
- ✅ Section components independently testable

### Rationale
- **Maintainability**: Smaller, focused components easier to understand and modify
- **Reusability**: Section components can be reused in other printer UIs
- **Testability**: Each section independently testable with clear props
- **Architecture**: Parent manages state/API, children render UI — clear separation of concerns
- **Consistency**: Shared status utility eliminates duplicate logic and ensures consistent colors

### Parallel Work Note
This work was completed independently by Ripley and Kane in parallel. Both implementations matched closely, validating the architectural approach was sound. Kane's implementation landed first; this decision documents the architectural pattern for future reference.
