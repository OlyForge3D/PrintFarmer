# Auto-Dispatch Documentation Updated with Known Issues & Configuration Guide

**Author:** Ash (Documentation Specialist)
**Date:** 2026-03-11
**Status:** COMPLETE — Documentation updated, ready for operator use

## Summary

Updated `docs/AUTO_DISPATCH.md` to reflect actual system behavior, document three critical bugs currently being fixed, and provide clear configuration guidance for operators.

## Key Changes

### 1. Added "Known Issues (Being Fixed)" Section
Documented three bugs that block auto-dispatch from working correctly:
- **Toggle Alone Doesn't Work:** UI toggle sets `AutoDispatchEnabled=true` but mode stays at `Manual` (seed default). System requires BOTH enabled + mode change.
- **PendingReady Gate Blocks First Upload:** Bed-clear banner only appears after print completion, preventing dispatch on first upload if printer stuck in PendingReady.
- **Frontend Naming Mismatch:** Frontend renamed autoPrint→autoDispatch, but API paths `/autoprint/` unchanged. Intentional backward-compatibility decision.

### 2. Refactored Configuration Section
Restructured for operators, not developers:
- **Three Independent Layers** mental model: System Toggle + System Mode + Per-Printer Opt-In
- **Step-by-step "How to Enable Auto-Dispatch"** with curl examples
- **Emphasized critical dependency:** `AutoDispatchMode` must be "Suggest" or "Auto" (NOT "Manual")
- **Clear per-printer opt-in:** Toggle ⚡ icon or use bulk enable

### 3. Updated API Documentation
- Clarified `/autoprint/` paths match backend `autoPrintEnabled` property
- Added note about frontend terminology "autoDispatch" vs API "autoprint"
- Improved PUT `/api/dispatch-settings` examples to show both required fields
- Added validation section for `autoDispatchMode` enum values

## Root Cause of Previous Gap

Original AUTO_DISPATCH.md was written before bugs were identified. It documented intended behavior, not actual behavior. Operators enabling the toggle would see nothing happen and have no explanation.

## Operator Impact

**Before:** Toggle auto-dispatch on → nothing happens → confusion
**After:** Docs explain exactly why (mode defaulted to Manual) + provide API workaround immediately

## Developer Impact

- Docs now match code behavior (mode guard in AutoDispatchBackgroundService)
- Clear troubleshooting path for "why didn't auto-dispatch work"
- Frontend naming (autoPrint→autoDispatch) explained for code reviewers

## Decision

**Do not change API paths or backend property names yet.** These changes would require:
- Database migration (Printer.AutoPrintEnabled → AutoDispatchEnabled)
- API endpoint path changes (breaking change for any integrations)
- Test updates across integration test suite

**Timing:** Backend property rename deferred to a future effort after bugs are fixed.

## Files Updated

- `docs/AUTO_DISPATCH.md` — Added known issues section, refactored configuration for operators, clarified API naming
- `.squad/agents/ash/history.md` — Added learning note documenting update
