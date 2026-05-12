# Lambert History

## Core Context

Lambert is the backend and infrastructure architect. Key retained context:
- Owns multi-database backend changes, background services, EF migrations, and most failure-detection / Obico runtime logic.
- Key backend pattern: singleton workers resolve scoped services via `IServiceScopeFactory`, and configuration-sensitive monitors should reread persisted settings rather than assume in-memory state.
- Prefers behavior-safe adapters: add compatibility for new upstream contracts without forcing migrations for older deployments, then protect the seam with focused tests.
- Important current references: `PrintFailureMonitorService`, `ObicoFailureDetectionService`, `ObicoServerController`, and the focused Obico controller/service test files.
- Per-toolhead slicer estimates: GcodeFile stores FilamentPerExtruderWeightG as a JSON string array, parse with System.Text.Json.JsonSerializer.Deserialize<double[]>.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-07 to 2026-03-16: Delivered major backend work across auto-dispatch, analytics, camera platform prep, initial failure detection, and multi-server Obico support.
- 2026-03-25: Normalized PendingReady backend state, clarified warmup/attention boundaries, separated runtime reachability issues from route bugs, and adapted Obico to the upstream GET-first contract.
- 2026-03-26: Implemented failure-detection incident history persistence, enriched frontend alerts with job context, finalized plugin gap analysis, and validated architecture principles.

## Learnings

- 2026-05-01: **Core One L Orca process profile compatibility.** OrcaSlicer 2.3.2 Prusa CORE One L/HF process profiles rely on multi-clause `compatible_printers_condition` expressions, including `printer_notes` stored as arrays on HF machine profiles and `printer_notes!~/.*HF_NOZZLE.*/` for non-HF profiles. The worker must normalize `printer_notes` arrays and support whitespace-separated `and`/`or` plus `!~` before caching process `CompatiblePrinters`, otherwise the New Slice Job process selector can be empty even when machine lookup succeeds.

- 2026-08-01: **PFarm1-pysq.5 — Backend snake_case verification.** Profile settings are stored as opaque JSON blobs (RawJson, SettingsJson, AdvancedSettings TEXT columns) with a few promoted typed columns. The `ProcessProfileDto.Settings` dictionary passes through keys verbatim — snake_case from OrcaSlicer flows end-to-end without translation. `CamelToNativeKeyMap` was already deleted (commit 68042d59). `HttpJobPollerService` passes override keys through directly. `OrcaProfilesService.ParseProcessProfile` reads snake_case keys natively. OrcaBundle export uses snake_case keys. SignalR slicer hubs only transmit high-level DTOs; profile settings are opaque payloads unaffected by hub serialization policy. All 463 slicer/profile tests pass. Report: `.squad/decisions/inbox/lambert-pysq5-verification.md`.
- 2026-08-01: **Cut Model geometry upload endpoint** — Added `POST /api/3d-models/upload-geometry` for lightweight STL uploads from the browser Cut Model tool. Uses `IFormFile`, stores in model upload directory with UUID filename, creates minimal `Model3D` DB entry (no hash dedup, no thumbnails, no analysis), returns `GeometryUploadResultDto` with `fileUrl` pointing to existing `GET /api/3d-models/file/{id}` endpoint. The slicer worker can HTTP-fetch this URL. New DTO in `Farm.Slicer.Module/Dtos/Model3DDtos.cs`, new method `UploadGeometryAsync` in `IModel3DFileService`/`Model3DFileService`, endpoint in `Model3DFilesController`. 200MB size limit. Pattern: temp-file write then move for safety, cleanup on failure.
- 2025-07-14: **Slicer backend audit completed.** The slicer subsystem is far more mature than expected: ~107 API endpoints across 13 controllers, separate SlicerDbContext with 10 tables, two SignalR hubs (`/hubs/slicer-registry` + `/hubs/slicers`), plugin-based integration loaded at runtime via `SlicerIntegrationExtensions`, and ~416 dedicated tests. The OrcaSlicer worker is fully functional — invokes the binary via CLI, runs a 7-stage pipeline, and reports progress via HTTP polling. Key architecture: slicer module can run embedded in main API or as standalone `Farm.Slicer.Host` (port 5246). Cross-domain references are soft (Guid-only, no FK). Only 1 migration per provider (InitialV1). Main gaps for frontend: no slicer settings CRUD endpoint, no job pagination/filtering, no manual retry endpoint, and profile editing limited to custom profiles only. Full audit in `.squad/decisions/inbox/lambert-orcaslicer-backend-audit.md`.
- 2026-04-01: **Slicer estimate snapshot at dispatch** — Added SlicerEstimateGrams to PrintJobToolheadUsage entity (nullable double). At job dispatch (DispatchJobAsync in PrintJobManagementService), new SnapshotSlicerEstimatesAsync method parses GcodeFile.FilamentPerExtruderWeightG JSON array and creates PrintJobToolheadUsage records with slicer estimates. Repository gained GetToolheadsForPrinterAsync and AddToolheadUsageAsync methods. Migrations created for both PostgreSQL and SQL Server. This enables frontend to show per-toolhead filament estimates for in-progress jobs before actual consumption data is available at completion. Pattern: parse JSON string with System.Text.Json.JsonSerializer.Deserialize<double[]>, iterate per-extruder weights, create usage records with toolhead spool/material/color denormalized from Toolhead entity, skip zero estimates.
- 2026-07-16: FlashForge MMU Phases 2, 3, 5 implemented. ADX5 firmware reports `Tool Count: 1` via M115 but exposes T0+T1 in M105 — `DetectExtruderCount` cross-references both and takes the MAX. `PrinterStatusDto` extended with optional `ExtruderTemperatures` and `DetectedExtruderCount` (null defaults for backward compat). `SyncMmuToolheadsOnEntity` creates/removes MmuGate virtual toolheads on MultiMaterial toggle — operates on already-loaded entity, caller saves. Pre-existing test file `MmuGateAutoCreationTests.cs` used wrong type names (`HotendModel` vs `HotendModelDefinition`) and wrong DbSet names — domain uses `*Definition` suffix consistently.
- 2026-03-27: The smallest operator-relevant printer session timeline is a printer-scoped read model, not new persistence: anchor sessions on `PrintJob`, compose nested events from `QueuedAt`/`DispatchedAt`/`ActualStartTime`/`ActualEndTime` plus `JobStateHistory` and `FailureDetectionIncident`, and attach orphan incidents by printer + session window when `JobId` is missing.
- 2026-03-26: `/api/failure-detection/status` already exposes the operator-facing monitoring reason/source/target/outcome contract. For richer PrintFarmer-owned UX, the safest backend addition is optional `jobName`/`fileName` on `FailureDetectionPrinterStatusDto` and SignalR `FailureDetectionDto`, sourced from `IPrinterStatusCacheReader` with queued-job fallback in `PrintFailureMonitorService`.
- 2026-03-26: Persisted failure-detection history is a narrow incident slice, not a generic audit system: `FailureDetectionIncident` stores only detected failures, `PrintFailureMonitorService` records them through scoped `IFailureDetectionIncidentHistoryService`, and `GET /api/failure-detection/history?printerId=&take=` returns newest-first `FailureDetectionDto` rows with optional persisted `id`.

- 6 tests for standard (monolith) mode
- 3 tests for microservices mode ensuring correct capability reporting
- Validates no side effects on other capabilities

### Files Changed
- `src/api/Program.cs` — SystemCapabilitiesController capability detection logic
- `src/tests/Farm.Web.Api.Tests/Integration/SystemCapabilitiesIntegrationTests.cs` — New regression test file

### Validation
- `/api/system/capabilities` now returns `slicingEnabled=true` in microservices mode
- Slicer-host routing verified via nginx
- All other capabilities reporting correctly

### Impact
Unblocked slicer UI visibility in Docker microservices deployments. Production deployment now shows slicer module to users.


## 2026-04-05: 3D Models Page Missing STLs — Spawn as Backend Lead

**Role:** Backend Architect
**Status:** 🔍 Investigation spawned

User reported STL uploads appear successful but files don't show on 3D Models page. Spawned Lambert for investigation of:

1. Upload endpoint persistence (`POST /api/models/upload`)
2. File persistence to disk/storage
3. Database entries creation
4. List endpoint contract (`GET /api/models`)
5. Server logging and silent failures

Working parallel with Ripley (frontend) and Kane (QA).

**Key files to review:**
- `src/api/Controllers/ModelsController.cs`
- `src/infra/Services/ModelService.cs`
- `src/infra/Data/Repository/ModelRepository.cs`
- Upload logging and exception handling

## 2026-04-04: Fixed 3D Models Not Appearing After Upload

### Problem
Users reported uploading .stl files successfully, but the 3D Models page showed no files.

### Investigation
- Traced data flow from upload endpoint (`/api/3d-models/upload` in `Model3DFilesController`) to listing endpoint (`/api/3d-models` GET)
- Discovered the slicer module uses a **separate database context** (`SlicerDbContext`) with its own schema
- Found that `SlicerDbContext` was never being initialized during application startup
- `Models3D` table was never created, so uploads failed silently

### Root Cause
The main `AppDbContext` has initialization logic in `DatabaseInitializationExtensions.cs` that calls `EnsureCreated()` for SQLite, but `SlicerDbContext` had no initialization. The slicer module was loaded, controllers registered, but the database schema was missing.

### Fix
Added `SlicerDbContext` initialization to the startup pipeline in `DatabaseInitializationExtensions.cs`:
1. Modified `InitializeDatabaseAsync` to accept optional `SlicerDbContext` parameter
2. Added schema initialization logic after main context initialization
3. Updated `ProgramHelpers.cs` to resolve and pass `SlicerDbContext` to the initializer

For SQLite: Uses `EnsureCreated()` (no migrations assembly exists)  
For PostgreSQL/SQL Server: Uses `Migrate()` (migrations assemblies exist)

### Files Changed
- `src/api/Infrastructure/DatabaseInitializationExtensions.cs` — Added slicer schema initialization
- `src/api/ProgramHelpers.cs` — Pass SlicerDbContext to initializer

### Validation
Tested locally:
- Deleted database
- Restarted API
- Verified Models3D table was created successfully
- Log output confirmed: "[Startup]   ✓ Slicer schema ensured (SQLite — no migration assembly)"

---

## 2026-04-05T16:17:29Z — Orchestration: Model Cleanup & Backend Mapping

**Spawned By:** Scribe (team coordination)  
**Coordination:** Ripley (frontend display), Kane (test coverage)

### Assignment

Backend data layer work for orphaned 3D model record cleanup and cross-context tag filtering:

1. **Orphaned Record Cleanup** — Identify and remove records from Apr 5 path migration (old relative paths)
2. **Schema Validation** — Ensure Model3D, Model3DTagMapping, and display name fields consistent
3. **Cross-Context Tag Filtering** — Design and implement tag filtering across AppDbContext → SlicerDbContext

### Success Criteria

✓ Orphaned records identified and cleanable without affecting valid models  
✓ Tag filtering query logic validated (cross-context join strategy confirmed)  
✓ Schema initialization includes both contexts, tag mappings verified  

### Related Decisions

- `.squad/decisions/decisions.md` — 3D Models Upload & Display multi-agent investigation
- `.squad/decisions/decisions.md` — Tag Filtering Implementation Gaps (deferred work item)
- `.squad/orchestration-log/2026-04-05T16-17-29Z-lambert.md` — Orchestration manifest

## 2026-04-05: OrcaSlicer Import Endpoint Implementation

**Role:** Backend Dev  
**Status:** ✅ Complete

### Task
Implemented the missing `POST /api/slicer/profiles/import/orca` endpoint to persist selected profiles from OrcaSlicer config bundles.

### Context
The frontend's `OrcaImportWizard` had a 4-step flow (upload → preview → review → import). The preview endpoint existed and worked, but the import endpoint was missing, causing 404 errors on final import.

### Implementation
1. **Updated DTOs** — Extended `ImportOrcaBundleDto` in `OrcaProfileModels.cs` with:
   - `SelectedPrinters?: List<string>` — Names of printer presets to import
   - `SelectedFilaments?: List<string>` — Names of filament presets to import
   - `SelectedProcesses?: List<string>` — Names of process presets to import

2. **Added Import Endpoint** in `ProfilesController.cs`:
   - Route: `[HttpPost("import/orca")]`
   - Authorization: `farm_admin` policy
   - Parses bundle JSON using `IOrcaBundleParsingService.ParseBundle`
   - Filters presets based on `SelectedPrinters`, `SelectedFilaments`, `SelectedProcesses` lists
   - Iterates through selected presets and calls `IProfilesService.ImportProfileAsync` for each
   - Returns `ImportOrcaBundleResultDto` with counts, warnings, and errors

3. **Error Handling**:
   - Returns 400 if `BundleJson` is null/empty or invalid format
   - Catches individual profile import failures and adds to warnings (continues processing)
   - Returns 500 for unexpected errors with logging

### Files Changed
- `src/slicer/Farm.Slicer.Module/Models/OrcaProfileModels.cs` — Added selection list properties to DTO
- `src/slicer/Farm.Slicer.Module.Api/Controllers/Slicing/ProfilesController.cs` — Added import endpoint

### Validation
- Build: ✅ 0 errors, 3 warnings (pre-existing StyleCop issues in OrcaSlicer worker)
- Tests: ✅ 2313 tests passed (463 slicer tests + 1850 API tests)
- Format: ✅ Applied dotnet format

### Key Architecture Decisions
- Import reuses existing `ImportProfileAsync` from `IProfilesService` — consistent deduplication via content hash
- Profiles are created as custom (non-system) profiles unless `AllowSystemOverride=true`
- Each preset type (printer/filament/process) is serialized from `RawParameters` dictionary for persistence
- Failures for individual presets don't abort entire import — warnings collected for user feedback

### Integration Points
- `IOrcaBundleParsingService` — Validates and parses bundle structure
- `IProfilesService.ImportProfileAsync` — Persists individual profiles with deduplication
- Frontend `OrcaImportWizard` — Now has complete upload → preview → import flow


## 2026-05-12 Session Wrap-Up

**Outcomes:** PFarm1-873d ✅ CLOSED, PFarm1-3sbh ✅ CLOSED, Changes Pushed  
**Deliverables:** Camera auto-upsert service, RTSP health probe, DB migrations  
**Tests:** All passing (schema, service, probe logic)  

### PFarm1-873d Implementation Summary

- Schema: BuddyCameraHost field (253 chars) on Printer entity
- Service: PrinterService upsert/delete logic for Camera entities
- Validation: IP/hostname only (reject URL schemes)
- DTOs: Updated UpdatePrinterDto, CreatePrinterFromDiscoveryDto, PrinterDto
- Migrations: PostgreSQL + SQL Server both valid
- Tests: Lifecycle (create/update/delete), validation, serialization

### PFarm1-3sbh Implementation Summary

- RTSP health probe: OPTIONS request to rtsp://{streamUrl}
- Fallback: TCP connect on port 554 if OPTIONS fails
- Integration: CameraHealthMonitorService dispatcher
- Tests: Success/failure, fallback, timeout handling
- Ready for: Continuous monitoring of Buddy and other RTSP cameras

### Integration Ready

- Buddy camera feature end-to-end ready in codebase
- No API breaking changes; additions only
- Foundation laid for downstream beads (snapshots, go2rtc)
- All changes pushed to remote

## 2026-05-12 go2rtc Sidecar Implementation — Session Complete

**Task:** PFarm1-lzf0 — Implement go2rtc sidecar for RTSP transcoding  
**Status:** ✅ CLOSED  
**Timestamp:** 2026-05-12T19:20:00Z

**Backend Changes:**
- Docker Compose: Added go2rtc service container
- Stream Registry: Implemented PrinterStreamRegistry manager for transcode URL resolution
- API Routes: Added /api/rtc/* handlers for stream transcoding requests
- Service Integration: Connected camera health monitor and stream dispatcher
- DTOs: UpdatePrinterDto includes transcodeStreamUrl for WebRTC output

**Validation:**
- ✅ All tests passing
- ✅ Build passing
- ✅ No new warnings
- ✅ go2rtc stream initialization verified

**Outcome:** go2rtc sidecar ready for RTSP-to-WebRTC transcoding. Bridge between Buddy/Prusa camera streams and frontend WebRTC viewers.

**[Older entries archived on 2026-05-12 — see history.md for recent updates]**
