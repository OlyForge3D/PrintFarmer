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
