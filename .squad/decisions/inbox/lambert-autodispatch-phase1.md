# Auto-Dispatch Phase 1 — Scored Suggestions

**Author:** Lambert (Backend Dev)
**Date:** 2026-03-07
**Status:** IMPLEMENTED — pending review

## What Was Built

Multi-factor dispatch scoring engine that evaluates every printer against a job's requirements and returns a ranked candidate list with full transparency into why each printer scored the way it did.

### New Files Created

| File | Purpose |
|------|---------|
| `src/infra/Services/Queue/Dispatch/DispatchModels.cs` | Core records: `DispatchScore`, `FactorScore`, `DispatchAction` enum, `DispatchMode` enum |
| `src/infra/Services/Queue/Dispatch/IDispatchScorer.cs` | Interface for scoring engine |
| `src/infra/Services/Queue/Dispatch/DispatchScorer.cs` | 9-factor scoring implementation |
| `src/infra/Services/Queue/Dispatch/IJobDispatchService.cs` | Interface for dispatch orchestration |
| `src/infra/Services/Queue/Dispatch/JobDispatchService.cs` | Orchestrator: scoring + assignment + audit logging |
| `src/infra/Services/Queue/Dispatch/DispatchDtos.cs` | API DTOs: `DispatchCandidateDto`, `FactorScoreDto`, `DispatchJobDto` |
| `src/infra/Domain/DispatchLog.cs` | Audit trail entity |
| `src/infra/Data/Configurations/DispatchLogConfiguration.cs` | EF Core configuration with indexes |

### Modified Files

| File | Change |
|------|--------|
| `src/infra/Domain/PrintJob.cs` | Added `DispatchedAt`, `DispatchScore`, `DispatchMode` properties |
| `src/infra/Data/AppDbContext.cs` | Added `DbSet<DispatchLog>` |
| `src/api/Controllers/JobQueueController.cs` | Added `IJobDispatchService` DI + 2 new endpoints |
| `src/api/Infrastructure/ServiceCollectionExtensions.cs` | Registered `IDispatchScorer` and `IJobDispatchService` |
| `src/tests/.../JobQueueControllerTests.cs` | Updated constructor to include new `IJobDispatchService` mock |

### API Endpoints

- `GET /api/job-queue/{id}/candidates` — Score all printers for a job, returns ranked list
- `POST /api/job-queue/{id}/dispatch-to` — Assign job to a specific printer and start print

### Scoring Factors

| # | Factor | Weight | Hard? | Logic |
|---|--------|--------|-------|-------|
| 1 | Material Match | 100 | YES | Loaded material → 100, supported → 50, no data → 30, unsupported → ELIMINATE |
| 2 | Nozzle Diameter | 100 | YES | Exact match ±0.01mm → 100, no data → 50, mismatch → ELIMINATE |
| 3 | Build Volume | 50 | No | Fits → 100, smaller → 20, no data → 70 |
| 4 | Enclosure | 80 | Conditional | Hard only when material needs enclosure |
| 5 | Nozzle Hardness | 80 | Conditional | Hard only when material is abrasive |
| 6 | Model Match | 60 | No | Exact → 100, same mfr → 50, no data → 70, different → 30 |
| 7 | Queue Depth | 30 | No | 0 jobs → 100, 1-2 → 70, 3-5 → 40, 6+ → 10 |
| 8 | Preferred | 40 | Conditional | In excluded list → ELIMINATE |
| 9 | Availability | 0 (pre-filter) | YES | Not available/in maintenance/disabled → ELIMINATE |

### Architecture Decisions

1. **DispatchScorer queries DbContext directly** rather than going through `IQueueRepository`. Scoring requires cross-entity joins (PrintJob → GcodeFile → PrinterModel, Printer → Toolheads → NozzleModel, FilamentType) that don't fit a single repository. This is a read-only query path.

2. **DTOs live with the service** in `src/infra/Services/Queue/Dispatch/` rather than in `src/infra/Dtos/`. The dispatch DTOs are tightly coupled to the scoring domain and unlikely to be reused elsewhere.

3. **Existing `/dispatch` endpoint preserved**. The new `/dispatch-to` endpoint handles scored dispatch (assign + score + audit + start). The existing endpoint handles direct dispatch without scoring.

4. **DispatchMode stored as int** on PrintJob, cast from the `DispatchMode` enum. Follows the project pattern for enum storage (e.g., `Printer.Backend`).

5. **No migrations created yet** — schema changes (DispatchLog table + PrintJob columns) need review before migration generation.

### TODO for Phase 2

- `PrinterGroup` entity for group-based compatibility scoring (currently uses model match as proxy)
- Auto-dispatch mode (system automatically dispatches to highest-scoring printer)
- Location proximity scoring (depends on hierarchical location system from Dallas's design)
- Batch-load filament types for all materials in a single query instead of per-job lookup

## Validation

- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 43 dispatch + controller tests pass (1467 total pass, 2 pre-existing LocationHierarchyTests failures)
- ✅ `dotnet format` clean
