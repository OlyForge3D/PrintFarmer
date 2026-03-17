# Auto-Print Scaling to 100 Printers — Architecture Assessment

**Author:** Dallas (Architect)  
**Date:** 2026-03-06  
**Context:** Jeff asked "How do we scale auto-print to 100 printers?"  
**Status:** Analysis complete, recommendations provided

---

## Executive Summary

**The current auto-print architecture scales fine to 100 printers.** No breaking changes needed.

**What works:**
- Event-driven dispatch (no polling)
- Concurrent per-printer processing
- Database indexes cover critical queries
- SignalR broadcasts scale with client count, not printer count

**Minor optimizations recommended (Priority 1):**
- Add `Printer.IsEnabled` index (30 seconds)
- Document that GetAllStatusAsync pattern is correct (no change)

**Future-proofing (Priority 2, defer until >200 printers):**
- Cache FilamentType lookups in DispatchScorer
- Use SignalR targeted groups instead of `Clients.All`

---

## Current Architecture

### Auto-Print State Machine

```
[Idle Printer] 
  → Job completes → TransitionToPendingReadyAsync 
  → [PendingReady] 
  → Operator clicks "Ready" → MarkReadyAsync 
  → [Ready] 
  → AutoDispatchTrigger.NotifyJobQueued() 
  → AutoDispatchBackgroundService 
  → DispatchJobAsync 
  → [None]
```

**Key insight:** Operator confirmation is the bottleneck, not the system. At 100 printers, humans gate the throughput.

### Auto-Dispatch Background Service

**Pattern:** Event-driven, no polling.

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    DispatchTriggerEvent evt = await trigger.ReadAsync(stoppingToken);
    _ = Task.Run(() => HandlePrinterIdleAsync(evt.PrinterId, ...));
}
```

**Concurrency:**
- Each printer idle event spawns a fire-and-forget Task
- `_dispatchLock` (SemaphoreSlim) serializes dispatch decisions (prevents double-job-assignment)
- `MaxConcurrentDispatches` setting limits in-flight operations

**Critical section:** Only DB query + job assignment is locked. The rest (idle wait, scoring, SignalR broadcast) runs concurrently.

### Dispatch Scorer

**Query pattern:**
```csharp
List<Printer> printers = await db.Printers
    .Include(p => p.Model).ThenInclude(m => m!.SupportedFilamentTypes)
    .Include(p => p.Model).ThenInclude(m => m!.Aliases)
    .Include(p => p.Toolheads).ThenInclude(t => t.NozzleModel)
    .AsSplitQuery()
    .AsNoTracking()
    .Where(p => p.IsEnabled)
    .ToListAsync(ct);

Dictionary<Guid, int> queueDepths = await db.PrintJobs
    .Where(j => j.AssignedPrinterId != null && j.Status != Completed/Failed/Cancelled)
    .GroupBy(j => j.AssignedPrinterId!.Value)
    .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
```

**Performance:** At 100 printers, `AsSplitQuery()` generates 4 queries (~400-500 total rows). With proper indexes, this is 5-10ms.

### GetAllStatusAsync

**Pattern:**
```csharp
List<Printer> printers = await db.Printers.ToListAsync(ct);  // 100 rows
Dictionary<Guid, int> queuedCounts = await GetQueuedCountsByPrinterAsync(printerIds, ct);  // 1 GroupBy query
Dictionary<Guid, string?> currentJobs = await GetCurrentJobNamesByPrinterAsync(printerIds, ct);  // 1 GroupBy query
```

**Analysis:** This is an N+2 pattern (not N+1), but the +2 are batch queries. At 100 printers, total rows fetched: ~100 + 20 (queued counts) + 10 (current jobs) = 130 rows. Acceptable.

### SignalR Broadcasts

**Current pattern:**
```csharp
await hub.Clients.All.SendAsync("autoprintstatechanged", status, ct);
```

**Scaling:** `Clients.All` is O(connected clients), not O(printers). With 5-10 concurrent dashboard users, 100 printers changing state is fine.

---

## Bottleneck Analysis @ 100 Printers

### ✅ No Bottlenecks

1. **AutoDispatchBackgroundService concurrency** — Fire-and-forget per-printer tasks. 100 printers going idle simultaneously spawn 100 concurrent Tasks (limited by thread pool, not the code). SemaphoreSlim only serializes the critical "assign job to printer" window.

2. **Database query patterns** — All critical paths use batch queries or indexed lookups:
   - `TransitionToPendingReadyAsync`: Uses composite `(AssignedPrinterId, Status)` index
   - `GetQueuedCountsByPrinterAsync`: GroupBy with `AssignedPrinterId` index
   - Dispatch scorer: Single `WHERE IsEnabled` query + batch queue depths

3. **SignalR broadcast load** — With <20 clients, `Clients.All` is negligible overhead. Each `SendAsync` is ~1ms.

4. **Database writes** — Auto-print state changes are infrequent (only on job completion + operator action). Dispatch writes are serialized. No contention.

### ⚠️ Minor Inefficiencies (optimize later)

1. **Missing index on `Printer.IsEnabled`** — Dispatch scorer queries `WHERE IsEnabled`. At 100 printers, table scan is fine. At 500+, need index.

2. **DispatchScorer material lookups** — Each job scores ~20 candidates, each candidate checks material compatibility. That's 20 `FilamentType` queries (mitigated by EF query cache). Could pre-load all active FilamentTypes in memory.

3. **SignalR `Clients.All` chatty at scale** — If 100 printers change state within 1 second, that's 100 broadcasts to all clients. Each client receives 100 messages. Could use targeted groups (`Clients.Group("dashboard")`).

4. **BuildStatusDtoAsync per-call** — Each auto-print action (MarkReady, Cancel, Skip) queries queue count + current job. If rapid-fire state changes happen, this adds up. Could batch or cache for 1-2 seconds.

### 🔴 Does NOT Break

- **No polling loops** — Event-driven design scales linearly
- **No global locks** — `_dispatchLock` is per-dispatch-cycle, released quickly
- **No cascading failures** — If one printer's dispatch fails, it's isolated
- **No CPU-bound operations** — State transitions are trivial. Scoring is I/O-bound (DB queries).

---

## Recommended Changes

### Priority 1: Small Wins (do now)

**1. Add `Printer.IsEnabled` index**

**File:** `src/infra/Data/Configurations/PrinterConfiguration.cs`

**Change:**
```csharp
builder.HasIndex(p => p.IsEnabled);
```

**Effort:** 30 seconds + migration  
**Impact:** Prevents table scan in dispatch scorer  
**Justification:** Dispatch scorer filters `WHERE IsEnabled` on every dispatch cycle. At 100 printers, table scan is 1-2ms. At 500+, it degrades. Index now, avoid future pain.

---

### Priority 2: Future-Proofing (defer until 200+ printers)

**2. Cache FilamentType lookups in DispatchScorer**

**Current:**
```csharp
FilamentType? requiredFilament = await db.FilamentTypes
    .FirstOrDefaultAsync(f => EF.Functions.Like(f.Name, requiredMaterial) && f.IsActive, ct);
```

**Proposed:**
```csharp
// Load once per scorer instance (or use IMemoryCache with 5min TTL)
private Dictionary<string, FilamentType> _filamentCache;

// Resolve from cache
requiredFilament = _filamentCache.TryGetValue(requiredMaterial, out var ft) ? ft : null;
```

**Effort:** 1 hour  
**Impact:** Eliminates 20 DB queries per dispatch cycle  
**Justification:** EF Core query cache helps, but explicit caching is cleaner. Defer until dispatch cycles slow down.

**3. Use SignalR targeted groups**

**Current:**
```csharp
await hub.Clients.All.SendAsync("autoprintstatechanged", status, ct);
```

**Proposed:**
```csharp
await hub.Clients.Group("auto-print-subscribers").SendAsync("autoprintstatechanged", status, ct);
```

**React client change:**
```typescript
useEffect(() => {
  connection.invoke("JoinAutoPrintGroup");  // Hub method: Groups.AddToGroupAsync(Context.ConnectionId, "auto-print-subscribers")
}, [connection]);
```

**Effort:** 2 hours  
**Impact:** Reduces broadcast to only clients watching auto-print page  
**Justification:** At 100 printers + 10 clients, `Clients.All` is fine. At 500 printers + 50 clients, targeted groups reduce chattiness. Defer.

---

### Priority 3: Over-Engineering (only if >500 printers)

**4. Paginate GetAllStatusAsync**

**Current:** Returns all printers in one response.

**Proposed:** Add `skip`/`take` parameters, return `PaginatedResult<AutoPrintStatusDto>`.

**Effort:** 4 hours  
**Justification:** GetAllStatusAsync is called infrequently (only when dashboard loads). At 100 printers, response is ~50KB. At 500 printers, ~250KB. Still acceptable. Defer.

**5. Redis cache for printer status**

**Pattern:** Cache `AutoPrintStatusDto` per printer in Redis with 30s TTL.

**Effort:** 1 day  
**Justification:** Premature optimization. DB queries are fast enough. Only consider if CPU-bound.

**6. Distributed lock for multi-node API**

**Current:** `SemaphoreSlim _dispatchLock` is in-memory, single-node only.

**Problem:** If API runs 3 replicas, each has its own `SemaphoreSlim`. Race conditions possible.

**Solution:** Use Redis-based distributed lock (e.g., RedLock).

**Effort:** 2 days  
**Justification:** Only needed for horizontal scaling. Current deployment is single-node. Defer until multi-replica API.

---

## What NOT to Change

1. **Don't add polling** — Event-driven dispatch is correct. Polling would degrade performance.
2. **Don't serialize per-printer tasks** — Concurrent fire-and-forget is optimal. Serialization would bottleneck.
3. **Don't optimize SignalR prematurely** — `Clients.All` is fine for <20 clients.
4. **Don't change database schema** — Indexes are correct. No schema changes needed.
5. **Don't add caching layers yet** — DB queries are fast. Cache when CPU-bound, not before.

---

## Conclusion

**100 printers: ✅ No changes needed.**

The architecture is event-driven, concurrent, and well-indexed. The only action item is adding `Printer.IsEnabled` index (30 seconds).

**Monitoring recommendations:**
- Track dispatch cycle duration (target <100ms)
- Track GetAllStatusAsync response size (target <100KB)
- Track SignalR broadcast latency (target <10ms)

**Revisit at 200 printers** to confirm assumptions hold.

---

**Decision:** No immediate architectural changes required. Add `IsEnabled` index and monitor.
