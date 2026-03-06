# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Sprint 1 Summary (2026-03-07)

**Completed:**
1. **Auto-Dispatch Phase 1** (1011s) — 9-factor scoring engine with DispatchScorer + JobDispatchService
   - DispatchLog audit entity tracks all dispatch actions
   - 2 API endpoints: GET /api/job-queue/{id}/candidates, POST /api/job-queue/{id}/dispatch-to
   - 43 dispatch + controller tests all passing
   - Schema changes pending review (PrintJob.DispatchedAt/Score/Mode + DispatchLog table)

2. **Location Hierarchy Full-Stack** (1298s) — Tree service + React components
   - LocationTreeService: GetTree, GetAncestors, GetDescendants, Move (with circular ref detection)
   - 4 API endpoints for tree operations
   - LocationTreePicker, LocationBreadcrumb, LocationManagement React components
   - 21 location hierarchy tests all passing

3. **Test Coverage** (1109s) — Pre-implementation test suites
   - 22 DispatchScorerTests (unit + edge cases + integration stubs)
   - 21 LocationHierarchyTests (service + API level)
   - All 43 tests passing against current codebase

**Key Decisions:**
- **Printer Groups recommended** for G-code dispatch (Dallas's Approach C) — user-curated groups of identical hardware
- **Printer assignment at ANY level** — not restricted to leaf nodes (per user feedback)
- **Location dashboards planned** — click location → show subtree printers with aggregated status
- **API refactoring Phase 1 complete** — apiClient.ts with shared auth + correlation ID infrastructure

### Controller-Repository Architecture Pattern (2025-03-05)
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
