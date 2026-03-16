# Orchestration: Dallas — Location Hierarchy Design (COMPLETED)

**Date:** 2026-03-06T19:44  
**Agent:** Dallas (Lead/Architect)  
**Status:** ✅ COMPLETED  
**Output:** `.squad/decisions/inbox/dallas-location-hierarchy-design.md` (23.8KB)

---

## Summary

Dallas completed the hierarchical location system design document. This is a comprehensive architectural decision that addresses Jeff's feedback on supporting multi-level location organization (e.g., Warehouse 1 > Room A > Rack 3).

### Key Deliverables

- **Problem Statement:** Flat Location model doesn't scale. Competitors show hierarchy is a market differentiator.
- **Current State Analysis:** Detailed audit of existing Location infrastructure (entity, FK, DB config, repository, service, DTOs, controller, frontend).
- **Competitive Analysis:** 6-competitor comparison showing most don't offer hierarchy; 3DPrinterOS does (but rigid).
- **Approach Comparison:** 
  - Approach A: Self-referential tree (adjacency list) — simple, recursive queries costly
  - Approach B: Materialized path — efficient reads, update-heavy on moves
  - Approach C: Path materialization cache — recommended balance
- **LocationType Entity:** User-defined node types (building, floor, room, rack, etc.) with inheritance support.
- **Tree API:** CRUD, depth checks, move operations, breadcrumb paths, dispatch-ready scoring hints.
- **Phase 1 Scope:** Core tree infrastructure. Phase 2: UI, dispatch integration, bulk operations.

### Quality Metrics

- **Scope:** Architected full system, not just DB schema
- **Risk Analysis:** Included migration strategy, backward compat, performance tradeoffs
- **Market Positioning:** Validated this is a real competitive advantage

---

## Next Steps (for team)

1. **Ripley (Frontend):** Implement LocationTreePicker component and breadcrumb rendering
2. **Lambert (Backend):** Implement DispatchScorer with location hierarchy weighting
3. **Kane (Tester):** Write integration tests for tree operations and move semantics
4. **Jeff (Product):** Review design for scope, validate LocationType taxonomy

---

## Files

- **Decision:** `.squad/decisions/inbox/dallas-location-hierarchy-design.md`
- **Status:** Ready for merge into decisions.md (pending Jeff's approval)
