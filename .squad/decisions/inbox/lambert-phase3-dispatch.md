# Auto-Dispatch Phase 3 — Batch Dispatch & Load Balancing

**Author:** Lambert (Backend Dev)
**Date:** 2026-03-07
**Status:** ✅ IMPLEMENTED — pending review

## Summary

Phase 3 adds batch dispatch capability and configurable load-balancing strategies to the auto-dispatch system. Operators can now dispatch multiple queued jobs at once, with the system distributing them across printers using one of three strategies.

## What Was Built

### 1. LoadBalancingStrategy Enum
- **BestFit** (default): assigns each job to its highest-scoring printer
- **RoundRobin**: cycles through eligible printers evenly
- **LeastBusy**: prefers printers with shortest queue depth (DB + in-batch tracking)

### 2. Batch Dispatch Service (`IBatchDispatchService`)
- `BatchDispatchAsync()` — thread-safe via `SemaphoreSlim`, respects `MaxConcurrentDispatches`
- `GetQueueStatusAsync()` — dashboard data: pending jobs, idle/busy printers, per-printer queue depth, 24h stats
- `GetDispatchHistoryAsync()` — paginated audit log with job/printer details

### 3. API Endpoints
| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/job-queue/batch-dispatch` | POST | Batch-dispatch multiple jobs (strategy override optional) |
| `/api/dispatch/queue-status` | GET | Fleet-wide dispatch dashboard data |
| `/api/dispatch/history` | GET | Paginated dispatch audit log |
| `/api/dispatch-settings` | GET/PUT | Now includes `LoadBalancingStrategy` |

### 4. SignalR Events
- `batchdispatchstarted` — emitted when batch begins (includes batch ID, job count, strategy)
- `batchdispatchcompleted` — emitted when batch finishes (dispatched/failed/skipped counts)

### 5. Schema Change
- `DispatchSettings.LoadBalancingStrategy` column added (string, max 20 chars, defaults to "BestFit")
- **EF Core migrations NOT YET generated** — pending this review

## Architecture Decisions

1. **Batch service is scoped** (not singleton) — uses AppDbContext which is scoped
2. **Static SemaphoreSlim** for batch lock — prevents concurrent batch operations from double-assigning jobs
3. **Strategy override per request** — batch dispatch request can override the system-wide strategy
4. **DispatchQueueStatusDto naming** — renamed from QueueStatusDto to avoid collision with existing `Farm.Infrastructure.QueueStatusDto`
5. **Dashboard queries are read-only** — all use `AsNoTracking()` for performance
6. **LeastBusy tracks batch assignments** — maintains a `Dictionary<Guid, int>` of in-batch assignments to correctly balance within a single batch

## Validation

- **Build:** 0 errors, 0 warnings
- **Tests:** 1952/1952 pass (1504 API + 448 slicer), 0 failures
- **Format:** `dotnet format` clean

## Next Steps

1. **Generate EF Core migrations** for `LoadBalancingStrategy` column (PostgreSQL + SqlServer)
2. **Kane:** Write Phase 3 tests (batch dispatch, round-robin distribution, least-busy balancing)
3. **Ripley:** Build batch dispatch UI (multi-select jobs → "Dispatch All" button, strategy selector)
4. **Future Phase 4:** Queue priority preemption, estimated completion time balancing
