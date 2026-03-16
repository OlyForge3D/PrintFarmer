# Orchestration: Sprint Launch — Auto-Dispatch + Location Hierarchy Phase 1

**Date:** 2026-03-06T19:44  
**Status:** ✅ LAUNCHED  
**Agents:** Lambert (Backend), Ripley (Frontend), Kane (Tester)

---

## Spawn Overview

Three agents launched simultaneously to deliver Phase 1 of two interconnected features: auto-dispatch scoring and location hierarchy.

### Lambert (Backend) — Auto-Dispatch Phase 1

**Scope:**  
- `DispatchScorer` service: Location proximity, nozzle compatibility, job age penalty, load balancing
- `IJobDispatchService`: New interface for dispatch orchestration
- `DispatchLog` entity: Audit trail for dispatch decisions
- New API endpoints: `/api/dispatch/score`, `/api/dispatch/history`

**Acceptance Criteria:**
- Scorer weights each candidate printer by location, nozzle, status, load
- Score API returns top 3 candidates with reasoning
- DispatchLog persists all decisions for audit
- All logic tested in isolation from UI

**Risk:** Location hierarchy may require re-scoring logic; will be addressed in Phase 2.

---

### Ripley (Frontend) — Location Hierarchy Pass 1

**Scope:**  
- `LocationType` CRUD (user-defined types: building, floor, room, rack)
- Tree data structure and `LocationTreePicker` component
- Breadcrumb rendering (Warehouse 1 > Room A > Rack 3)
- Drag-drop support for reassigning printers within tree

**Acceptance Criteria:**
- Tree picker renders hierarchy correctly
- Create/edit LocationType in modal
- Move nodes (with parent constraints)
- Breadcrumb updates on selection
- Printer selector shows tree instead of flat dropdown

**Risk:** Tree picker UX complexity; will validate with Kane's test suite.

---

### Kane (Tester) — Test Suite for Both Features

**Scope:**  
- Unit tests: `DispatchScorer` algorithm (location weight, nozzle compatibility, load)
- Integration tests: `LocationTreePicker` component (render, create, move, reorder)
- Contract tests: Dispatch API response shapes vs. frontend expectations
- Performance tests: Location path queries (materialized path cache efficiency)

**Acceptance Criteria:**
- 80%+ line coverage on DispatchScorer
- 75%+ component coverage on LocationTreePicker
- API contract tests validate camelCase serialization
- No flaky tests; all pass consistently

---

## Coordination Points

1. **Location ID in DispatchLog:** Lambert's DispatchLog needs LocationId reference once Ripley's tree is live
2. **Tree API Response Format:** Ripley's frontend needs camelCase breadcrumb paths from Lambert's API
3. **Nozzle Compatibility:** Both features depend on printer nozzle metadata; ensure Printer.Nozzle is populated before Phase 1 ends

---

## Timeline

- **Phase 1 Delivery:** 2026-03-13 (1 week)
- **Review & Validation:** 2026-03-14
- **Phase 2 (Dispatch Integration):** 2026-03-20

---

## Files

- **Backend:** New controllers in `src/api/Controllers/DispatchController.cs`
- **Frontend:** New components in `src/features/locations/components/LocationTreePicker.tsx`
- **Tests:** New test projects under `src/tests/`
