# Team Decisions

## BatchDispatchService Bug Fixes (2026-03-07)

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-07  
**Status:** ✅ FIXED — Build verified

### Bugs Fixed

#### 1. N+1 Query in DispatchLeastBusyAsync (HIGH)

**Problem:** Queue depth DB query executed inside foreach loop — N jobs = N DB round-trips.  
**Fix:** Hoisted query before the loop. `batchAssignments` dictionary continues to track in-batch state correctly.  
**Impact:** Batch dispatch of 50 jobs now makes 1 queue-depth query instead of 50.

#### 2. Divide-by-Zero in Average Score (MEDIUM)

**Problem:** `.Average()` called on potentially empty sequence after filtering dispatch logs with `.Where(l => l.Score.HasValue)`.  
**Fix:** Used `.DefaultIfEmpty(0).Average()` for safe fallback to 0.  
**Impact:** `GET /api/dispatch/queue-status` no longer throws when all recent dispatch logs have null scores.

### Team Takeaway

These are patterns to watch for in code review:
- **DB queries inside loops** = N+1 problem. Hoist and track in-memory.
- **`.Average()` without empty guard** = runtime exception. Always use `.DefaultIfEmpty()` or `.Any()` check.

### File Changed

`src/infra/Services/Queue/Dispatch/BatchDispatchService.cs`

---

## Code Review Lessons Learned (from batch dispatch fixes)

**2026-03-07** — Lambert & Ripley session

1. **Backend (C#):**
   - **N+1 pattern:** Any DB query inside a loop over batch items is a red flag. Query once, hoist before loop, track in-memory adjustments for within-batch changes.
   - **Empty sequence guards:** `.Average()`, `.Min()`, `.Max()` on LINQ results filtered by `.Where()` can throw `InvalidOperationException` on empty sequences. Always use `.DefaultIfEmpty(fallback)` or `.Any()` check.

2. **Frontend (TypeScript):**
   - **Backend enum serialization:** C# enums serialize as STRING values (via `JsonStringEnumConverter`), not numeric. TypeScript uses `string` type with string literal values: `'BestFit'`, `'RoundRobin'`, `'LeastBusy'`.
   - **Type sync:** When C# adds a field to an API DTO, the TypeScript interface must be updated immediately to prevent silent data loss on round-trips.

**Commit:** 3806a374 (`fix: resolve N+1 query, divide-by-zero, and missing TS field in dispatch`)

---

## Sprint 4 Scope Decisions (2026-03-07)

**Author:** Jeff Papiez (via Dallas scoping session)  
**Date:** 2026-03-07  
**Status:** ✅ APPROVED — User directive answers captured

### Key Decisions

1. **Printer Groups Membership:** A printer belongs to exactly ONE group (mutually exclusive enforcement)
2. **G-Code Backward Compatibility:** G-code without PrinterGroupId still dispatches normally (no group = any printer)
3. **Location Subtree Query:** Clicking a location includes printers at that level AND all descendants
4. **DispatchLog Retention Policy:** Keep forever (audit trail + regulatory compliance)
5. **API Refactor Phase 2 Scope:** Extract exactly 3 services (printers, queue, catalog). Phase 3 handles full implementation migration.

### Impact on Sprint 4

- **Printer Groups:** Requires validation logic (1:1) at entity save time
- **Location UI:** Recursive query optimization needed for large hierarchies
- **API Refactor:** Delegate pattern chosen to maintain 100% backward compat + zero test changes

### Files Affected

- `src/infra/Data/Entities/PrinterGroup.cs` (validation)
- `src/Web/ReactApp/src/services/printerService.ts` (group delegation)
- `src/Web/ReactApp/src/services/jobQueueService.ts` (queue delegation)
- `src/Web/ReactApp/src/services/catalogService.ts` (catalog delegation)

---

## API Service Refactor Phase 2 (2026-03-07)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-07  
**Status:** ✅ IMPLEMENTED

### Decision: Delegate Pattern

Extract 3 service modules (printerService, jobQueueService, catalogService) using **delegate pattern** to match existing locationService/cameraService conventions.

### Why Delegate

1. **Consistency** — locationService and cameraService already use this pattern
2. **Backward Compatibility** — All 1,196 tests pass without modification
3. **No Private Exposure** — axios instance stays private on ApiClient; full implementation move deferred to Phase 3
4. **Incremental** — New code imports from focused files; old code unchanged

### What Was Created

| Service | Lines | Methods | Domain |
|---------|-------|---------|--------|
| printerService.ts | 315 | 53 | CRUD, control, discovery, history, files |
| jobQueueService.ts | 169 | 28 | Queue ops, dispatch, analytics |
| catalogService.ts | 273 | 49 | Manufacturers, models, components, filaments |

### Phase 3 Prerequisite

To move implementations (not just delegate):
1. Extract axios instance + interceptors to shared `apiClient.ts`
2. Export axios for services to use directly
3. Services call `axios.get()` instead of delegating
4. Remove methods from ApiClient class (cleanup)

### Impact

- ✅ Zero test changes required
- ✅ api.ts barrel re-exports all 3 for backward compat
- ✅ New code should prefer specific service imports
- ✅ Code SRP improved (monolithic → modular)
