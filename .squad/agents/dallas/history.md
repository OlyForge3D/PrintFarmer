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
