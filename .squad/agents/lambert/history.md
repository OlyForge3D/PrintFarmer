# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Sprint 2 Summary (2026-03-07)

**Completed:**
1. **Auto-Dispatch Phase 2 Backend** (Lambert, agent-20) — Event-driven background service with Channel-based idle trigger, per-printer idle timer, SemaphoreSlim concurrency control, DispatchSettings singleton entity, Suggest/Auto modes, SignalR events (jobautodispatched, dispatchsuggestion, dispatchfailed)
   - Files: DispatchSettings entity, AutoDispatchTrigger (Channel reader), AutoDispatchBackgroundService (IHostedService), DispatchSettingsController (GET/PUT)
   - 1917 API tests passing, 0 failures, 0 new warnings
   - No EF migrations yet — pending review

2. **Auto-Dispatch Phase 2 Tests** (Kane, agent-21) — Pre-implementation validation suite with concurrent safety tests
   - 35 tests across 3 files: AutoDispatchBackgroundServiceTests (12), DispatchSettingsControllerTests (12), AutoDispatchConcurrencyTests (11)
   - Race condition tests: two-printers-same-job, multi-printer uniqueness, max-concurrent enforcement
   - Full suite: 1952 tests passing (1504 API + 448 slicer), 0 failures

3. **Location Hierarchy UI Tests** (Kane, agent-22) — Full component test suite per Jeff's mandate
   - 78 tests across 6 test files: LocationTreePicker (19), LocationBreadcrumb (11), LocationManagement (21), LocationSelector (8), PrinterLocationDragDrop (12), LocationManagementAdminPage (3)
   - Covers rendering, CRUD, interactions, error/loading/empty states, accessibility
   - All passing, fulfills user directive: "UI tests for all new UI features"

**Key Decisions:**
- **Event-driven Channel** over polling for idle notifications (no DB thrashing)
- **Suggest + Auto modes** for gradual automation trust-building
- **SemaphoreSlim atomicity** prevents double-assignment race conditions
- **DispatchSettings singleton** for type-safe configuration (not JSON key-value)
- **UI test policy**: Every new component must have Vitest + RTL coverage (Jeff's directive, now team standard)

**Learnings:**
- **FluentAssertions v8** removed `BeLessOrEqualTo()` — use `BeInRange` instead
- **Mock child components** when testing parents (isolation)
- **Button text in spans** — use `getByRole` over `getByText` for disabled-state checks
- **Dynamic mock imports** — `await import()` after `vi.mock()` for typed access
- **ConfirmationModal** renders inline (no portal, works with waitFor)

### Previous: Controller-Repository Architecture Pattern (2025-03-05)
- **Controllers should never directly inject AppDbContext** — all database access flows through repositories/services
- Controllers remain thin: receive request → call service → return response
- Services contain business logic and coordinate repository calls
- Repositories encapsulate data access and return domain entities/DTOs
- Statistics aggregations belong in dedicated service layer (`IStatisticsService`)
- Printer existence checks use `IPrintersRepository.ExistsAsync()`
- Webhook CRUD operations use `IWebhookRepository` for all database operations
- Service registration follows pattern: repository interface/implementation in `RegisterRepositories()`, service interface/implementation in `AddPrintFarmerServices()`
- File locations: repositories in `src/infra/Repositories/{domain}/`, services in `src/infra/Services/{domain}/`
- DTOs for controller responses live in `src/infra/Dtos/` for reusability across layers

### Auto-Dispatch Scoring Engine — Phase 1 (2026-03-07)
- **Dispatch scoring lives in `src/infra/Services/Queue/Dispatch/`** — co-located with queue services, separate subfolder for the dispatch domain
- **DispatchScorer queries DbContext directly** (not via repository) — scoring needs cross-entity joins (PrintJob+GcodeFile+Printer+Toolheads+FilamentType) that don't fit a single repository pattern. This is intentional for complex read-only queries.
- **PrintJob entity extended** with `DispatchedAt`, `DispatchScore`, `DispatchMode` (int backing `DispatchMode` enum) — no migration yet, schema change pending review
- **DispatchLog entity** added for audit trail — `src/infra/Domain/DispatchLog.cs`, EF config in `src/infra/Data/Configurations/DispatchLogConfiguration.cs`
- **API endpoints** added to `JobQueueController`: `GET /api/job-queue/{id}/candidates` (scoring), `POST /api/job-queue/{id}/dispatch-to` (assign+start)
- **Existing dispatch endpoint preserved**: `POST /api/job-queue/{id}/dispatch` still handles direct dispatch via `IPrintJobManagementService`
- **9-factor scoring algorithm**: Material (100), Nozzle (100), BuildVolume (50), Enclosure (80), NozzleHardness (80), ModelMatch (60), QueueDepth (30), Preferred (40), Availability (pre-filter)
- **Hard elimination factors**: Material, Nozzle, Availability always eliminate on mismatch. Enclosure/Hardness conditional on material needs.
- **Phase 2**: Printer Groups integration (G-code compatibility), auto-dispatch mode (system auto-assigns), location proximity scoring


### Auto-Dispatch Phase 2 — Background Service & Auto-Dispatch on Idle (2026-03-07)
- **DispatchSettings singleton entity** added — `src/infra/Domain/DispatchSettings.cs`, EF config with `HasData` seeding (Id=1). Controls: `AutoDispatchEnabled`, `AutoDispatchMode` (Manual/Suggest/Auto), `IdleThresholdSeconds`, `MinimumScoreThreshold`, `MaxConcurrentDispatches`.
- **AutoDispatchMode enum** added in `src/infra/Services/Queue/Dispatch/DispatchModels.cs` — Manual (0), Suggest (1), Auto (2). Distinct from `DispatchMode` which tracks how a job was assigned.
- **Event-driven architecture via Channel<T>**:
  - `AutoDispatchTrigger` (singleton) wraps a `BoundedChannel<Guid>` — fire-and-forget from scoped services, consumed by background service.
  - `AutoDispatchBackgroundService` (IHostedService) reads from channel, waits idle threshold with per-printer cancellable CTS, then dispatches.
  - `CancelPendingDispatch(printerId)` cancels the idle timer if printer goes offline before threshold elapses.
- **Thread safety**: `SemaphoreSlim(1,1)` serializes the dispatch-decision window so two printers going idle simultaneously cannot grab the same job. `Interlocked` tracks in-flight count for `MaxConcurrentDispatches`.
- **Scoring reuse**: Iterates unassigned queued jobs in priority order, calls existing `IDispatchScorer.ScorePrintersForJobAsync()` for each, checks if idle printer qualifies above threshold. No scoring logic duplication.
- **Suggest mode**: Scores and emits `dispatchsuggestion` SignalR event but does NOT dispatch. Operator dispatches manually. Logged as `DispatchAction.Suggested` in audit trail.
- **Auto mode**: Scores, dispatches via `IJobDispatchService.DispatchJobAsync()`, sets `DispatchMode = Auto`, emits `jobautodispatched` SignalR event.
- **Hook point**: `PrintJobCompletionService.MarkCurrentJobAsCompletedAsync()` calls `IAutoDispatchTrigger.NotifyPrinterIdle(printerId)` after job completion (fire-and-forget, does not block completion flow).
- **SignalR events**: `jobautodispatched`, `dispatchsuggestion`, `dispatchfailed` — all broadcast to All clients via `IHubContext<PrinterHub>`.
- **API endpoints**: `GET /api/dispatch-settings`, `PUT /api/dispatch-settings` — `DispatchSettingsController` with validation.
- **Service registration**: Trigger singleton in `ServiceCollectionExtensions.AddPrintFarmerServices()`, background service in `RegisterBackgroundServices()` (respects `disableBackgroundServices` flag).
- **No EF migrations created** — entity + configuration only, schema change pending review.

### EF Core Migrations — Location & Dispatch Entities (2026-03-07)

**Migration:** `AddLocationDispatchEntities` (PostgreSQL: `20260307145233`, SqlServer: `20260307145247`)

**What changed:**
- Location table: added ParentId (self-referential FK, Restrict), Path, Depth, SortOrder, TotalPrinterCount columns; replaced unique `IX_Locations_Name` with composite `IX_Locations_ParentId_Name`
- DispatchLog table created with FKs to PrintJobs/Printers (Cascade), indexes on PrintJobId, PrinterId, CreatedAtUtc
- DispatchSettings singleton table with HasData seed (auto-dispatch OFF)
- PrintJob columns: DispatchedAt, DispatchScore, DispatchMode

**Commands used:**
```bash
cd src
DB_PROVIDER=postgres dotnet ef migrations add AddLocationDispatchEntities \
  --project ./migrations/Farm.Migrations.PostgreSQL/Farm.Migrations.PostgreSQL.csproj \
  --startup-project ./migrations/Farm.Migrations.PostgreSQL/Farm.Migrations.PostgreSQL.csproj \
  --context AppDbContext

DB_PROVIDER=sqlserver dotnet ef migrations add AddLocationDispatchEntities \
  --project ./migrations/Farm.Migrations.SqlServer/Farm.Migrations.SqlServer.csproj \
  --startup-project ./migrations/Farm.Migrations.SqlServer/Farm.Migrations.SqlServer.csproj \
  --context AppDbContext
```

**Learnings:**
- EF tooling v10.0.2 works fine against runtime v10.0.3 (just a warning)
- Entity configurations via `IEntityTypeConfiguration<T>` in `Data/Configurations/` are auto-discovered by `ApplyConfigurationsFromAssembly`
- DesignTimeDbContextFactory uses `--startup-project` pointing to the migration project itself (not API)
- Both providers require `DB_PROVIDER` env var set for correct factory selection

### Auto-Dispatch Phase 3 — Batch Dispatch & Load Balancing (2026-03-07)
- **LoadBalancingStrategy enum** added in `DispatchModels.cs` — BestFit (0, default), RoundRobin (1), LeastBusy (2)
- **DispatchSettings** extended with `LoadBalancingStrategy` property, EF config stores as string, seed defaults to BestFit
- **IBatchDispatchService + BatchDispatchService** — core batch dispatch logic in `src/infra/Services/Queue/Dispatch/`
  - `BatchDispatchAsync()` — dispatches multiple queued jobs with configurable strategy, thread-safe via `SemaphoreSlim`
  - BestFit: pick highest-scoring printer per job; RoundRobin: cycle through eligible printers; LeastBusy: track DB + in-batch queue depth
  - Respects `MaxConcurrentDispatches` from `DispatchSettings`
  - SignalR events: `batchdispatchstarted`, `batchdispatchcompleted` broadcast to all clients
  - `GetQueueStatusAsync()` — returns pending jobs, idle/busy printer counts, per-printer queue depth, 24h dispatch stats
  - `GetDispatchHistoryAsync()` — paginated dispatch log with job/printer names, scores, actions
- **API Endpoints:**
  - `POST /api/job-queue/batch-dispatch` — batch dispatch with optional strategy override (`JobQueueController`)
  - `GET /api/dispatch/queue-status` — dispatch dashboard queue status (`DispatchController`)
  - `GET /api/dispatch/history?page=1&pageSize=20` — paginated dispatch history (`DispatchController`)
- **DTOs added to DispatchDtos.cs:** BatchDispatchRequest, BatchDispatchResult, BatchDispatchItemResult, DispatchQueueStatusDto, PrinterQueueDepthDto, DispatchStatsDto, DispatchHistoryDto, BatchDispatchStartedEvent, BatchDispatchCompletedEvent
- **DispatchSettingsDto/UpdateDispatchSettingsDto** — extended with `LoadBalancingStrategy` property
- **DispatchSettingsController** — updated GET/PUT to include `LoadBalancingStrategy`
- **Service registration** — `IBatchDispatchService` → `BatchDispatchService` as scoped in `ServiceCollectionExtensions`
- **Existing test fix** — `JobQueueControllerTests` constructor updated with `IBatchDispatchService` mock
- **Naming collision resolved:** renamed new `QueueStatusDto` → `DispatchQueueStatusDto` to avoid collision with existing `Farm.Infrastructure.QueueStatusDto`
- **No EF migrations created yet** — `LoadBalancingStrategy` column pending migration generation
- **Build:** 0 errors, 0 warnings; **Tests:** 1952/1952 pass (1504 API + 448 slicer)

### BatchDispatchService Bug Fixes (2026-03-09)

**Fixed two code-review bugs in `src/infra/Services/Queue/Dispatch/BatchDispatchService.cs`:**

1. **N+1 Query in DispatchLeastBusyAsync (HIGH):** Queue depth DB query was inside the foreach loop over jobs — dispatching N jobs triggered N separate DB round-trips. Moved the query before the loop; in-batch assignments via `batchAssignments` dictionary still correctly adjust effective queue depth per printer as jobs are dispatched within the batch.

2. **Divide-by-Zero in Average Score (MEDIUM):** `GetQueueStatusAsync` called `.Average()` on dispatch logs filtered to `.Where(l => l.Score.HasValue)` without guarding against an empty sequence. If all dispatched logs have null scores, this throws `InvalidOperationException`. Fixed with `.DefaultIfEmpty(0).Average()` which safely returns 0 when no scored logs exist.

**Learnings:**
- **N+1 pattern in batch loops:** Any DB query inside a loop over items being processed should be a red flag. Hoist queries before the loop and track in-memory state for within-batch changes.
- **`.Average()` on empty sequences:** Always use `.DefaultIfEmpty(fallback).Average()` or check `.Any()` before calling `.Average()` on LINQ sequences that could be empty after filtering.
- **Build:** 0 errors, 0 new warnings
