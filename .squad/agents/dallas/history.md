# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Architecture Review — 2026-03-06

**Solution stats:** 1,566 C# files across 30 projects. 585 TS/TSX files in React frontend. 64 entity configs in EF Core. 64 controllers. 26 API project references.

**Key architectural patterns identified:**
- Backend plugin architecture with `IBackendClientPlugin` / `IExtendedBackendPlugin` interfaces in `backends/Farm.Backend.Plugin.Core/`
- Discovery framework with confidence-scored probing in `discovery/`
- Unit of Work + Repository pattern in `infra/` (AppUnitOfWork wrapping 8 repositories)
- Feature-folder organization on React frontend (20 feature modules)
- Multi-database provider support (SQLite/PostgreSQL/SQL Server) via `DB_PROVIDER` env var
- Separate migration projects per provider in `migrations/`
- Settings architecture with `IAppSetting` (runtime/DB) vs `ISystemSetting` (bootstrap/config)

**Dependency flow:** Backend.Plugin.Core ← Infra ← Discovery/Backends ← API. No circular deps.

**Anti-patterns found:**
- 3 controllers inject AppDbContext directly (StatisticsController, MaintenanceScheduleDeploymentController, WebhooksController)
- `api.ts` is 3,458 lines — god file
- `shared/` project is empty (stale .NET 9 artifacts), `signalr/` and `prusaslicer-worker/` directories exist but aren't in solution
- Backend plugins reference both Plugin.Core AND Infra (wider coupling than necessary)
- API project has 26 project references (heavy coupling at the composition root)
- Mixed path separator styles in csproj files (cosmetic but sloppy)

**Key file paths:**
- Solution: `src/farm-web.sln`
- API entry: `src/api/Program.cs` (~700 lines, modularized via extension methods)
- DbContext: `src/infra/Data/AppDbContext.cs` (80+ DbSets)
- Plugin core: `src/backends/Farm.Backend.Plugin.Core/`
- Frontend API client: `src/Web/ReactApp/src/services/api.ts` (3,458 lines)
- Frontend routing: `src/Web/ReactApp/src/App.tsx`

### Feature Planning Deep Dive — 2026-03-06

**Job queue & dispatch infrastructure is more mature than expected:**
- `PrintJob` already has `RequiredNozzleDiameter`, `RequiredMaterialType`, `RequiredCapabilities`, `PreferredPrinterIds`, `ExcludedPrinterIds` — all needed for dispatch scoring
- `GcodeFile` has parsed metadata: `RequiredNozzleDiameter`, `RequiredMaterial`, `PrinterModelId`, `EstimatedPrintTimeMinutes`, `EstimatedFilamentWeightG`
- `Printer.AutoPrintEnabled` / `AutoPrintState` fields exist but dispatch logic not yet implemented
- `IQueueRepository.GetAvailablePrintersAsync()` and `GetCompatiblePrintersAsync()` exist
- `PrintJobCompletionService` already detects idle printers — natural hook for auto-dispatch

**Printer capability model is comprehensive:**
- Toolhead entity: nozzle model (diameter, type, hardness), hotend, extruder, supported materials
- FilamentType entity: `IsAbrasive` (requires hardened nozzle), `NeedsEnclosure`
- PrinterModel: `SupportedFilamentTypes` collection, hardware feature booleans
- NozzleModelDefinition: diameter, type enum (Brass/HardenedSteel/StainlessSteel/etc.)
- Component model chain: Printer → Toolhead → NozzleModel → NozzleType → can determine abrasive compatibility

**Statistics infrastructure — good foundation, clear gaps:**
- 5 existing endpoints: summary, jobs-over-time, cost-over-time, filament-by-material, printer-utilization
- `PrintJobStatistics` one-to-one entity with temperature, cost, duration, success/failure
- `PrinterStatistics` cumulative entity with total hours/jobs/filament
- `IPrintCostCalculator` service exists for cost estimation
- Frontend: 8 KPI cards + 4 Recharts visualizations in `features/statistics/`
- **GAP:** No idle/offline time tracking — no way to calculate utilization %
- **GAP:** No electricity/depreciation cost factors
- **GAP:** No CSV/PDF export capability
- **GAP:** StatisticsController injects DbContext directly (bypasses repository pattern)

**Key paths for new feature work:**
- Dispatch services: `src/infra/Services/Queue/` (new `Dispatch/` subfolder)
- Statistics services: `src/infra/Services/Statistics/StatisticsService.cs`
- Cost calculator: `src/infra/Services/Queue/` (IPrintCostCalculator)
- Job completion: `src/infra/Services/Queue/PrintJobCompletionService.cs` (idle detection hook)
- Frontend stats: `src/Web/ReactApp/src/features/statistics/`
- Frontend queue: `src/Web/ReactApp/src/features/queue/`

**Architecture decisions made:**
- Auto-dispatch uses weighted scoring algorithm (not rule-based) — more flexible, tunable per-farm
- Uptime tracking via periodic snapshots (5-min interval) rather than event-driven — simpler, tolerates server restarts
- Analytics and auto-dispatch are independent feature tracks — can be built in parallel
- New entities: DispatchSettings (singleton config), DispatchLog (audit), PrinterUptimeSnapshot (time-series), CostConfiguration (singleton config)
- Plan written to: `.squad/decisions/inbox/dallas-feature-plan-autodispatch-analytics.md`

### G-Code Printer Specificity Constraint — 2026-03-07

**Critical feedback from Jeff:** G-code is NOT portable across printers, even identical models. Firmware versions, nozzle diameters, hardware modifications, acceleration curves, and start/end sequences are **baked in at slice time**. Running a G-code file sliced for Printer A on Printer B causes print failures, quality loss, or physical damage.

**Impact on auto-dispatch:**
- Original plan's "Printer Model Match" factor (weight 60) was dangerously naive — it treated G-code as somewhat portable
- **Root issue:** Presence of `PrinterModelId` in GcodeFile metadata doesn't guarantee cross-printer compatibility
- **Solution:** Introduce **Printer Groups** — user-curated sets of truly identical/interchangeable printers
- Each `GcodeFile` stores `PrinterGroupId` (not single PrinterId); dispatch only to printers in that group
- Makes dispatch safe by construction: farmers assert group membership, system enforces compatibility
- Future enhancement: On-demand re-slicing (Approach B) for farms that want cross-group load balancing (requires slicer integration, accepts latency)

**Revised dispatch scoring:** Group compatibility is now a HARD elimination factor (score 0 if mismatch), not a soft weight.

**Decision:** Implement Printer Groups (Approach C) in Phase 1; reserve on-demand slicing (Approach D) as optional future enhancement.

**Plan written to:** `.squad/decisions/inbox/dallas-dispatch-gcode-compatibility.md`

### Hierarchical Location System Design — 2026-03-07

**Current Location model is well-built but flat:** Location is a first-class entity (not a string on Printer). Full CRUD stack: entity → repository → service (475 lines) → controller (228 lines) → DTOs → AutoMapper → frontend components (LocationManagement, LocationSelector, PrinterLocationDragDrop). Printer has optional `Guid? LocationId` FK with `DeleteBehavior.SetNull`.

**Key files for Location system:**
- Entity: `src/infra/Domain/Location.cs` (27 lines)
- DB config: `src/infra/Data/Configurations/LocationConfiguration.cs` (36 lines)
- Service: `src/infra/Services/Locations/LocationService.cs` (475 lines)
- Repository: `src/infra/Repositories/Locations/EfLocationRepository.cs` (182 lines)
- DTOs: `src/infra/Dtos/LocationDtos.cs` (73 lines)
- Controller: `src/api/Controllers/LocationsController.cs` (228 lines)
- Frontend management: `src/features/catalog/components/LocationManagement.tsx` (300 lines)
- Frontend selector: `src/features/catalog/components/LocationSelector.tsx` (68 lines)
- Frontend drag-drop: `src/features/printers/components/PrinterLocationDragDrop.tsx` (150+ lines)
- Frontend services: `src/Web/ReactApp/src/services/locationService.ts` + `printerLocationService.ts`
- Printer assignment endpoints on `PrintersController`: `POST /api/printers/{id}/location`, `DELETE /api/printers/{id}/location`

**Architecture decision: Adjacency List + Cached Path (Hybrid).** Self-referential `ParentId` FK for structural integrity, plus computed `Path` column for fast queries and breadcrumbs. Rejected nested sets (too complex) and closure table (overkill for expected tree sizes < 100 nodes). Trees will be shallow (3-5 levels typically).

**Breaking change identified:** Current Location.Name has a GLOBAL unique index. Hierarchy requires changing to composite unique on (ParentId, Name) so "Rack 1" can exist under different parent rooms.

**New entity proposed: LocationType** — user-defined types (Building, Room, Rack, etc.) with icon/color for UI. 7 system-seeded types. Users can add custom types.

**PrinterGroup is independent from Location.** They serve different purposes (G-code compatibility vs physical organization). No schema coupling. UI convenience shortcut: "Create group from printers in this location."

**Migration is fully backward-compatible:** All new columns have defaults or are nullable. Existing locations become root nodes. Existing API responses unchanged without opt-in query params.

**Competitor gap confirmed:** Most competitors (SimplyPrint, Bambu, OctoFarm, FlowQ) have NO location hierarchy. 3DPrinterOS has fixed 3-level hierarchy. Nobody offers user-defined location types. This is a real differentiator.

**Decision written to:** `.squad/decisions/inbox/dallas-location-hierarchy-design.md`

### Sprint 4 Scope Definition — 2026-03-09

**Scoped and documented all 4 sprint items with full breakdown:**

1. **Printer Groups** (G-code safety) — PrinterGroup entity + FKs on Printer/GcodeFile. Hard-elimination factor in DispatchScorer. 15 backend files, 4 frontend components + tests. ~40 story points.

2. **Auto-Dispatch EF Migrations** (schema ready) — DispatchSettings + DispatchLog entities. Migrations for PostgreSQL, SQL Server, MySQL. ~15 story points.

3. **Location Dashboards** (Phase 2 UX) — GET /api/locations/{id}/printers/subtree endpoint. LocationDetailPage + printer list component. ~25 story points.

4. **API Service Refactor Phase 2** (code quality) — Extract printerService, jobQueueService, catalogService. Barrel export pattern. ~10 story points (no new functionality, pure refactoring).

**Total: ~90 story points. Duration: 5–7 calendar days (optimal parallelization).**

**Critical path identified:** Item 2 (migrations) should land first (soft dependency for Item 1). Item 4 can start Day 1 in parallel. Items 1 & 3 can run concurrently after Item 2 merges.

**Execution order:** Day 1: Start Items 2 + 4 in parallel. Day 2: Start Item 1 (backend) + Item 3 (backend). Days 3–6: Frontend work. Day 7: Testing + polish.

**Open questions for Jeff:** Printer group cardinality (1:1 vs N:N), G-code backward compat, dashboard subtree scope, DispatchLog retention, API refactor Phase 2 boundaries.

**Document written to:** `.squad/decisions/inbox/dallas-sprint4-scope.md` (25.7 KB, comprehensive breakdown with file paths, dependencies, test strategies, risk mitigation)

### Sprint 4 Day 1 (2026-03-07) — Sprint Planning & Architecture Sign-Off

**Status:** ✅ COMPLETE (DRAFT awaiting user approval) — Orchestration log: `.squad/orchestration-log/2026-03-07T2144-dallas-sprint4.md`

**Deliverable:** Finalized comprehensive Sprint 4 scope document addressing all 4 epic items.

**Sprint 4 Four-Item Breakdown (26.3 KB document):**

1. **Printer Groups (G-code Safety) — ~89 story points**
   - PrinterGroup entity (Id, Name, Description, CreatedDate, UpdatedDate)
   - FKs on Printer (nullable) + GcodeFile (nullable)
   - API endpoints: CRUD operations + printer assignment
   - Hard-elimination dispatch scoring factor
   - UI: Printer group assignment modal, auto-grouping helpers
   - Tests: Entity constraints, API routes, integration scenarios

2. **Auto-Dispatch EF Migrations (Schema Ready) — ~34 story points**
   - DispatchLog extended: +6 fields (InitiatorUserId, DispatchStrategyUsed, BatchId, RetryCount, ErrorMessage, ExecutionTimeMs)
   - DispatchSettings entity finalized (AutoDispatchEnabled, PreferredStrategy, MaxConcurrentDispatches, IdleThresholdMinutes, UpdatedAt)
   - DispatchStatus enum (Pending, InProgress, Success, Failed, RetryScheduled)
   - Migrations for PostgreSQL, SQL Server, SQLite
   - Status: **DELIVERED (2026-03-07 by Lambert)**

3. **Location Dashboards Phase 2 (UX + Analytics) — ~156 story points**
   - Backend: GET /api/locations/{id}/printers/subtree with pagination/filtering
   - Frontend: LocationDetailPage (tabs: overview, printers, analytics, settings)
   - Components: LocationPrinterList, LocationAnalyticsPanel, LocationQuickStats
   - Subtree scope: Clicked level + all descendants
   - Tests: Query performance (N+1 guard), component interactions, accessibility

4. **API Service Refactor Phase 2 (Code Quality) — ~81 story points**
   - printerService.ts: 53 methods (CRUD, control, discovery, history, files)
   - jobQueueService.ts: 28 methods (queue ops, dispatch, analytics)
   - catalogService.ts: 49 methods (manufacturers, models, components, filaments)
   - Pattern: Delegate (consistent with locationService, cameraService)
   - Status: **DELIVERED (2026-03-07 by Ripley)**

**Total Scope:** ~360 story points. Duration: 2–3 weeks with parallelization.

**User Decision Answers (Captured from Jeff Papiez):**
1. Printer groups: 1:1 mapping (mutually exclusive)
2. G-code backward compat: Without group = dispatch normally
3. Location subtree: Clicked level + all descendants
4. DispatchLog retention: Keep forever (audit trail)
5. API refactor Phase 2: Exactly 3 services; Phase 3 handles full implementation

**Team Assignments:**
- **Lambert** (Backend): Printer Groups entity + migration, dispatch scoring integration
- **Ripley** (Frontend): Location dashboards UI, printer group management UI, API refactor
- **Kane** (QA): Integration testing, end-to-end dispatch scenarios, location drag-drop validation

**Dependency Chain & Critical Path:**
- **Hard blocker:** Item 2 (migrations) must land before Item 1 backend work
- **Parallel tracks:** Item 4 (API refactor) independent, can start Day 1
- **Soft dependency:** Item 1 backend informs Item 1 frontend
- **Execution:** Day 1: Items 2 + 4 parallel. Day 2+: Items 1 + 3 parallel. Days 3–6: Frontend. Day 7: QA.

**Key Decisions Documented:**
- Printer groups are domain-enforced (1:1 at entity save time, not just UI validation)
- Location subtree requires efficient recursive query (N+1 guard in GET endpoint)
- API refactor uses delegate pattern to maintain 100% backward compat + zero test changes
- Phase 3 prerequisites clearly documented (axios extraction, implementation move)

**File Status:** 
- ✅ DRAFT COMPLETE (26.3 KB specification document)
- ⏳ PENDING USER APPROVAL (awaiting Jeff sign-off on scope and priorities)
- 📋 DECISION INBOX MERGED to decisions.md (2026-03-07)

**Artifacts:**
- Sprint scope: `.squad/decisions/decisions.md` (merged from inbox)
- User answers: Captured in `copilot-directive-sprint4-answers.md` (merged)
- Session summary: `.squad/log/2026-03-07-sprint4-day1.md`
- Architecture decisions: `decisions.md` section "Sprint 4 Scope Decisions"

### Analytics Architecture — 4 Missing Features (2026-03-09)

**Task:** Architect comprehensive implementation plan for 4 analytics features identified by Brett's competitive analysis.

**Context:** Brett found PrintFarmer has solid analytics foundations (StatisticsService with 5 endpoints, 8 KPI cards, 4 Recharts charts) but lacks Export/Reporting, Unified Dashboard, Correlation Charts, and Predictive Alerts that competitors offer.

**Key findings from codebase examination:**

1. **Existing infrastructure is robust:**
   - `StatisticsController` has 5 endpoints (summary, jobs-over-time, cost-over-time, filament-by-material, printer-utilization)
   - `StatisticsService` implements all core aggregation logic with proper date filtering
   - Frontend: `StatisticsPage` with 8 KPI cards + 4 Recharts visualizations (JobsOverTimeChart, CostOverTimeChart, FilamentByMaterialChart, PrinterUtilizationChart)
   - TanStack Query hooks: `useStatisticsSummary`, `useJobsOverTime`, `useCostOverTime`, `useFilamentByMaterial`, `usePrinterUtilization`
   - `recharts` v3.6.0 already installed and extensively used
   - `jspdf` v4.2.0 and `jspdf-autotable` v5.0.7 already installed (can leverage for PDF export)
   - `react-csv` v2.2.2 already installed (can leverage for CSV export)

2. **Data model is complete for advanced analytics:**
   - `PrintJob` has all fields needed: `Status`, `RequiredMaterialType`, `AssignedPrinterId`, `ActualPrintTime`, `ActualFilamentUsage`, `ActualCost`, `FailureReason`
   - `PrintJobStatistics` one-to-one entity with temperature data (`ActualHotendTemp`, `ActualBedTemp`, `PrintDurationMinutes`)
   - `PrinterStatistics` cumulative entity with `TotalPrintHours`, `TotalJobsCompleted`, `TotalFilamentUsed`
   - Join paths exist for correlation queries (PrintJob → PrintJobStatistics, PrintJob → Printer)

3. **Anti-patterns avoided:**
   - `StatisticsController` injects `DbContext` directly (bypasses repository pattern) — but this is acceptable for read-only analytics queries
   - Frontend chart components follow consistent pattern: props interface with `data`, `isLoading`, `error`
   - API client methods follow consistent pattern: `apiClient.get(endpoint)` with TanStack Query hooks

**Architecture decisions:**

1. **Export/Reporting:** New `IReportExportService` with PDF (QuestPDF) and CSV (CsvHelper) generation. Separate endpoints for each export type. Frontend: `ExportMenu` component with blob download pattern.

2. **Unified Dashboard:** Frontend consolidation only. New `BusinessAnalyticsDashboard` page with tabs (Overview, Jobs, Costs, Printers). Reuses all existing endpoints. Keep original `StatisticsPage` as simpler view.

3. **Correlation Charts:** New `ICorrelationAnalyticsService` with 5 methods: material success rates, printer × material performance, temperature vs quality, duration trends, failure reasons. New `CorrelationAnalyticsController` with 5 endpoints. Frontend: 5 new Recharts components in new `CorrelationAnalyticsPage`.

4. **Predictive Alerts:** New `IPredictiveAnalyticsService` with heuristic-based prediction (no ML yet). Methods: job failure prediction, maintenance forecast, active alerts. New `PredictiveAnalyticsController` with 3 endpoints. Frontend: `AlertPanel`, `MaintenanceForecastPanel`, `JobRiskPredictor` components integrated into unified dashboard.

**Build order:** All 4 features are independent and can be built in parallel. No blocking dependencies. Recommended Lambert order: Correlation → Export → Predictive (complexity ascending). Recommended Ripley order: Unified Dashboard → Correlation Charts → Export UI → Predictive UI.

**New backend services:**
- `IReportExportService` / `ReportExportService` (Feature 1)
- `ICorrelationAnalyticsService` / `CorrelationAnalyticsService` (Feature 3)
- `IPredictiveAnalyticsService` / `PredictiveAnalyticsService` (Feature 4)

**New backend controllers:**
- `StatisticsController` extended with export endpoints (Feature 1)
- `CorrelationAnalyticsController` (Feature 3)
- `PredictiveAnalyticsController` (Feature 4)

**New backend DTOs:**
- `ReportDtos.cs`: `ReportRequest`, `JobHistoryCsvRow`
- `CorrelationAnalyticsDtos.cs`: 5 DTOs for correlation data
- `PredictiveAnalyticsDtos.cs`: 7 DTOs for prediction/forecast/alerts

**New frontend pages:**
- `BusinessAnalyticsDashboard.tsx` (Feature 2)
- `CorrelationAnalyticsPage.tsx` (Feature 3)

**New frontend components:**
- 13 new components for charts, tabs, exports, alerts, forecasts

**New frontend hooks:**
- `useCorrelationAnalytics.ts` with 5 hooks
- `usePredictiveAnalytics.ts` with 3 hooks

**Dependencies to add:**
- Backend: `QuestPDF` v2024.12.4, `CsvHelper` v33.0.1
- Frontend: None (all libraries already installed)

**Effort estimate:** ~104 hours total (~13 days with parallelization). Lambert: ~36 hours. Ripley: ~44 hours. Testing: ~24 hours.

**Plan written to:** `.squad/decisions/inbox/dallas-analytics-architecture.md` (62.5 KB comprehensive specification with exact file paths, endpoint signatures, component names, DTO definitions, and code examples).

**Key reusability win:** Existing `recharts` library handles all new correlation charts. No new chart library needed. All new services extend existing `StatisticsService` patterns. No architectural disruption.

## Analytics Architecture Planning (2026-03-09)

**Decision:** PFarm1-analytics-001  
**Status:** ✅ CLOSED  
**Output:** Comprehensive analytics architecture plan (1,910 lines)

Architected 4 parallel analytics features based on competitive analysis:
- Export/Reporting with PDF (QuestPDF) + CSV (CsvHelper)
- Unified Analytics Dashboard with correlation charts
- Performance Correlation Analysis (5 endpoints)
- Predictive Maintenance Alerts (3 endpoints)

**Team assignments:** Lambert (backend services + 12 endpoints), Ripley (frontend components + hooks), Kane (49 integration tests).

**Key Technical Decisions:**
- Feature separation: `/analytics` dashboard separate from `/statistics` KPI overview
- Parallel development roadmap with no blocking dependencies
- Reuse existing `recharts` library and React Query patterns
- QuestPDF 2025.1.0 + CsvHelper 33.0.1 for export capabilities

**Outcome:** All 4 features implemented, tested, and ready for production.
