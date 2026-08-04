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


---

## [Archived 2026-05-21 by Scribe — full ## Learnings section before Phase 1 closeout summarization]

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

## SpeedMultiplier SignalR Propagation (PFarm1-00u1)

**Task**: Wire `SpeedMultiplier` from `SimplePrinterStatus` through the Prusa backend pipeline to `PrinterStatusDto` and out via SignalR.

**Changes**:
- Added `int? SpeedMultiplier = null` to `PrusaCompositeStatus` record
- Added `int? SpeedMultiplier = null` to `PrinterStatusDto` record
- Updated `PrusaLinkClient.GetCompositeStatusAsync()` to pass `status.Printer.Speed` through
- Updated `PrusaLinkPollingService` SignalR broadcast mapping
- Updated `PrusaLinkStatusClient` on-demand status mapping
- Added `speedMultiplier?: number` to frontend `PrinterJobInfo` TypeScript interface

**Data flow**: PrusaLink API `Printer.Speed` → `SimplePrinterStatus.SpeedMultiplier` → `PrusaCompositeStatus.SpeedMultiplier` → `PrinterStatusDto.SpeedMultiplier` → SignalR `printerupdated` → frontend `speedMultiplier`

**Key learnings**:
- Positional records with default `null` parameters are backward-compatible additions
- Other backends (Moonraker, OctoPrint, FlashForge, SDCP) don't currently expose speed multiplier through their status pipelines — field will be null for those

---

### Event-Driven Camera Snapshots (PFarm1-y3n1)

**What**: Implemented event-driven camera snapshot capture on print start, complete, and fail events. Snapshots are fetched from camera SnapshotUrl, stored on filesystem, and tracked in the database with print job association.

**Files created**:
- `src/infra/Domain/CameraSnapshot.cs` — Entity with PrinterId, CameraId, PrintJobId, EventType, FilePath, CapturedAt, FileSizeBytes
- `src/infra/Services/Cameras/ICameraSnapshotService.cs` — Interface with `CaptureSnapshotAsync(printerId, eventType, printJobId)`
- `src/infra/Services/Cameras/CameraSnapshotService.cs` — Implementation: fetches JPEG via named "CameraSnapshot" HttpClient, stores to `{snapshotRoot}/{printerId}/{jobId}/{timestamp}_{event}_{cameraId}.jpg`
- `src/infra/Data/Configurations/CameraSnapshotConfiguration.cs` — EF Core fluent config with indexes and FK behaviors
- `src/api/Controllers/CameraSnapshotsController.cs` — API endpoints: `GET by-job/{id}`, `GET by-printer/{id}`, `GET {id}/image`, `DELETE {id}`

**Files modified**:
- `src/infra/Data/AppDbContext.cs` — Added `DbSet<CameraSnapshot>`
- `src/infra/Services/StorageManagement/IStoragePathService.cs` — Added `GetSnapshotStorageDirectory()`
- `src/infra/Services/StorageManagement/StoragePathService.cs` — Implemented snapshot dir (env: SNAPSHOT_STORAGE_PATH, config: STORAGE_PATHS:SNAPSHOTS, default: {content}/snapshots)
- `src/infra/Services/Printers/PrintJobCompletionService.cs` — Added ICameraSnapshotService as optional dep; captures on PrintCompleted and PrintFailed
- `src/api/Services/PrintQueue/PrintJobManagementService.cs` — Added ICameraSnapshotService as optional dep; captures on PrintStarted
- `src/api/Infrastructure/ServiceCollectionExtensions.cs` — Registered ICameraSnapshotService + named HttpClient "CameraSnapshot" (10s timeout)

**Architecture decisions**:
- Optional nullable constructor parameter pattern (consistent with existing deps like INotificationService, IAutoTagService)
- Fire-and-forget with try/catch — snapshot failure never blocks print status updates
- Named HttpClient "CameraSnapshot" with 10s timeout for snapshot fetching
- Filesystem storage path follows existing IStoragePathService pattern (env var → config → default)
- CameraSnapshotDto excludes FilePath for security; image served via dedicated endpoint

**Key learnings**:
- AppDbContext uses `ApplyConfigurationsFromAssembly` — must create IEntityTypeConfiguration<T> for entity to get indexes/FK behaviors
- DbSet alone creates table but without configured indexes or delete behaviors
- Controller methods returning Task must have Async suffix (VSTHRD200 analyzer enforced as error)
- PrintJobCompletionService has 12+ optional deps — adding one more follows established pattern cleanly

## Recent

_Last 5 most-recent learnings preserved from full history. Entries before 2026-05-26 archived to history-archive.md._

**Summary of detailed entries 2026-05-26 to 2026-07-13:**
- **2026-05-29 through 2026-06-03:** Delivered control-gate pattern for home endpoints (PR #316), camera integration (go2rtc sidecar, event snapshots, RTSP health), speed multiplier SignalR, Buddy camera support, OrcaSlicer import endpoint, external-reference adoption research, notification provider pattern, filament cost tracking (Spoolman), and artifact metadata endpoint.
- **2026-05-31:** External-reference-app feature sweep produced three adoption candidates: notification providers (8-provider backend, Phase 3), electricity cost tracking (smart plugs), and Printables.com import.
- **2026-06-01 through 2026-06-03:** System info API pattern, Printables selected-file import, Refit certificate upgrade, WebAuthn/FIDO2 passkey ceremony endpoints (Fido2 v4 package, challenge cache, verification patterns).
- **2026-06-03 through 2026-07-13:** Heading typography scope, settings IA query model, triple-model review consensus, slicer profile depth learnings.

**Current Assignment:** Issue #708 Backend v3 revision (Hicks REQUEST_CHANGES handoff). Five blockers: APNs token redaction, JWT invalidation test, rate-bucket race, attention prefs, capabilities JSON casing. Previous OrcaSlicer work paused.

### Recent Detailed Entries

- **2026-05-29 — PR #316 Merged (Bishop, SHA 8becf256).** Control-gate pattern applied to `/home`, `/homexy`, `/homez` endpoints. Conflict resolved (test union merge). Next: extend pattern consistency across remaining endpoints (backlog priority).
- **2026-05-12 — go2rtc Sidecar Implementation (PFarm1-lzf0).** Added go2rtc service container to docker-compose, `PrinterStreamRegistry` for transcode URL resolution, `/api/rtc/*` route handlers, and `transcodeStreamUrl` on `UpdatePrinterDto`. Bridges Buddy/Prusa RTSP cameras to frontend WebRTC viewers.
- **2026-05-12 — Event-Driven Camera Snapshots (PFarm1-y3n1).** New `CameraSnapshot` entity + `ICameraSnapshotService` capturing JPEG snapshots on `PrintStarted`, `PrintCompleted`, `PrintFailed`. Storage layout `{snapshotRoot}/{printerId}/{jobId}/{timestamp}_{event}_{cameraId}.jpg`. Optional nullable constructor injection pattern; fire-and-forget try/catch so snapshot failures never block print status updates. Named `HttpClient "CameraSnapshot"` (10s timeout). Use `IEntityTypeConfiguration<T>` (not just DbSet) to get configured indexes/FK behaviors via `ApplyConfigurationsFromAssembly`.
- **2026-05-12 — SpeedMultiplier SignalR propagation (PFarm1-00u1).** `int? SpeedMultiplier` added to `PrusaCompositeStatus`, `PrinterStatusDto`, and frontend `PrinterJobInfo`. Wired through `PrusaLinkClient.GetCompositeStatusAsync()` + `PrusaLinkPollingService` SignalR broadcast. Other backends (Moonraker, OctoPrint, FlashForge, SDCP) leave it null until their pipelines surface speed. Positional records with default-null params remain backward-compatible additions.
- **2026-05-12 — Buddy Camera + RTSP probe (PFarm1-873d / PFarm1-3sbh).** `BuddyCameraHost` field (253 chars, IP/hostname-only) on `Printer`; `PrinterService` upserts/deletes companion `Camera` entities on update. RTSP health probe uses OPTIONS via `rtsp://{streamUrl}` with TCP-port-554 fallback; integrated into `CameraHealthMonitorService` dispatcher. Migrations created for both PostgreSQL and SQL Server.
- **2026-04-05 — OrcaSlicer import endpoint.** `POST /api/slicer/profiles/import/orca` (`farm_admin` policy) parses bundle JSON via `IOrcaBundleParsingService.ParseBundle`, filters by `SelectedPrinters` / `SelectedFilaments` / `SelectedProcesses`, calls `IProfilesService.ImportProfileAsync` per preset (content-hash dedup applies). Per-preset failures collected as warnings; whole-import failures return 500. Closes the missing leg of the `OrcaImportWizard` upload→preview→import flow.

- **2025-11-24 — Error-translation test pattern for plugin backends (PR #318 review).** When testing plugin error translation (firmware rejection → `PrinterBackendBusyException` → controller outcome), test the **full mutation path end-to-end**, not just the helper logic in isolation. Example: mock `StartPrintAsync` to throw on rejection, call the actual mutation, assert exception propagated — don't just test the parsing helper separately. Helper correctness is compile-time verifiable; the seam (backend rejects → exception raised → controller maps) is the critical contract that needs integration-level validation. Applies to all three backends (Moonraker, SDCP, FlashForge) symmetrically.

- **2025-11-24 — Real-transport test pattern for plugin backends (PR #318 fix-up).** Spinning up Kestrel WebSocket for SDCP + TcpListener for FlashForge exercises the full rejected-mutation → status-roundtrip → exception propagation path. Much higher fidelity than mocking the helper layer. Tests `SdcpClientBusyTests` and `FlashForgeClientStartPrintBusyTests` validate the seam (backend rejects → exception raised → controller maps to outcome) end-to-end. Ack=1 + CurrentStatus=[1] → busy; code 9 (starting) → busy; code 0 (idle) → false (SDCP); ~M23 rejection + BUILDING_FROM_SD → busy; BUILDING → busy; READY → false (FlashForge). All 6 behavior-level tests pass; `dotnet format --verify-no-changes` clean.

- **2026-05-28 — Gate pattern reused for home endpoints (#314, PR #316):** The `GatePrinterControlAsync` + `MapControlOutcome` pattern in `PrintersController` is the canonical way to add status-gated control to any printer endpoint. Pattern lives at lines ~2122–2155. The three home handlers (`/home`, `/homexy`, `/homez`) previously returned `bool` from service methods — gate sits in front and short-circuits with 409 before the service call; the `bool` result mapping stays unchanged after the gate.

- **2026-05-28 — Backend busy-error propagation:** Plugin-specific firmware signals (HTTP 409/503 for Moonraker; status round-trip on Ack for SDCP; `~M119` echo for FlashForge) translated into `PrinterBackendBusyException`. Moonraker `SendGcodePrivateAsync` throws on HTTP 409/503; SDCP round-trips status on Ack failure; FlashForge echoes `~M119` check on rejection. All backends map to `BackendBusy` → 502 Bad Gateway. Archived older learnings for this pattern (2025-11-23, 2025-11-24) in history-archive.md.

- **2026-05-28 — Plugin-propagation gap deferred (follow-up #317):** Moonraker, SDCP, and FlashForge plugins do not translate firmware busy responses into `PrinterBackendBusyException`. Controller gate is sufficient as primary defense; race-condition gap tracked as P2 in issue #317.

### External-reference-app Review Pointer — 2026-05-31

external-reference-app repo ([external reference repo]) was reviewed by Brett. Two adoption candidates identified: gcode-preview (toolpath rendering) and client-side 3MF parsing. See decisions.md entries "Consider G-code toolpath preview parity from external-reference-app" and "Consider a richer slice progress contract" for details.

## Team Assignment: External-reference-app Adoption Plan (Scribe Merge, 2026-05-31)

**Incoming Work:** Notification system backend (Phase 3, ~4 work items).

**Context from Research:**
- external-reference-app implements 8-provider notification system: email, Telegram, Discord, generic webhook, ntfy, Pushover, CallMeBot/WhatsApp, Home Assistant
- IProvider pattern identified: `backend/app/schemas/notification.py` ProviderType enum + `backend/app/services/notification_service.py` dispatch logic
- PrintFarmer phased rollout: webhook + Discord + Telegram first; remaining providers in follow-up PRs
- Print farm users demand notifications on their preferred channel (often Telegram/Discord, not email)

**Phase 3 Deliverables (scheduled, not yet assigned to sprint):**
1. Create `INotificationProvider` interface (webhook, Discord, Telegram implementations)
2. Add `NotificationPreferences` entity + EF migration
3. Implement `NotificationService` dispatcher
4. Integrate with existing print lifecycle (completion, failure, queue empty events)

**Linked Decisions:** decisions.md entries "External-reference-app Feature Adoption" and "External-reference-app Feature Sweep — Top Adoption Candidates"


---

### External-reference-app Adoption Finalization — 2026-05-31

**Brady Confirmation:** Notification providers (webhook + Discord + Telegram) ship as ONE PR (Phase 3 ready for scheduling).

**Spoolman Cost Source Confirmed:** Filament cost priority: Spoolman price first, per-material fallback second.

**Backend Stubs Incoming (Backlog Priority):**

1. **Electricity Cost Tracking (Smart Plugs) — ~5 days backend work**
   - New entity: `PowerMonitor` (config per printer)
   - New time-series table: `PowerReading` (watts + timestamp)
   - New providers: `ISmartPlugProvider` (Kasa, Tasmota, Shelly)
   - Hosted service: `PowerMonitorPollingService` (per-printer loop, 10 s intervals)
   - Job completion trigger: `IPowerAggregationService` (kWh aggregation, cost calculation)
   - Add `KwhUsed` to `PrintJob` + migrations
   - Admin CRUD + graph endpoints

2. **Printables.com Import Service — ~2 days backend work**
   - New service: `IPrintablesImportService` (GraphQL fetch, CDN download)
   - New endpoints: `POST /api/3d-models/import-url/preview`, `POST /api/3d-models/import-url`
   - Add `SourceUrl`, `SourceLicense`, `SourceCreator`, `ImportedAt` to `Model3DFile` entity + migrations
   - MakerWorld deferred (blocker: Bambu Cloud token auth)

3. **Passkey (WebAuthn) Login — ~4 days backend work**
   - New NuGet: `Fido2NetLib`
   - New entity: `UserPasskeyCredential` (credential storage + audit)
   - New service: `IPasskeyService` (ceremony orchestration, challenge cache)
   - New endpoints: register/begin, register/complete, login/begin, login/complete, credentials list, revoke
   - Add `AuthMethod` field to login audit

**Linked Decisions:** decisions.md entries "Backlog: Electricity Cost Tracking via Smart Plugs", "Backlog: Printables.com Model Import", "Backlog: Passkey (WebAuthn) Login Support"

## Learnings

- **2026-06-03T12:42:57-07:00 — Settings shell layout and heading guardrails.** The settings shell should inherit its height from the routed layout container (`h-full` / `flex-1 min-h-0`) instead of viewport `calc()` math. Keep a visible `h1` plus a visible, focusable active-pane `h2`, and scope the global Bebas heading treatment to `h1`/`h2` so `h3`-`h6` stay opt-in.

- **2026-06-03 — Settings accessibility patterns.** `ThemeSwitcher` in `src/Web/ReactApp/src/common/components/ThemeSwitcher.tsx` now follows the ARIA radio-group model with roving `tabIndex` and wraparound Arrow/Home/End navigation, so only the selected theme is tabbable. `CommandPalette` in `src/Web/ReactApp/src/features/settings/components/CommandPalette.tsx` uses an input-driven `combobox` + `listbox` + `aria-activedescendant` pattern with non-button `role="option"` rows, keeping typing focus in the filter field while arrows move the active result.

- **2026-05-31 — Printables import foundation (#349, PR #375).** `IPrintablesImportService` + `PrintablesGraphQLClient` + `GET /api/3d-models/printables/preview?url=` in `Farm.Slicer.Module.Api`. URL parsing via compiled `Regex` — accepts `/model/{id}` and `/model/{id}-{slug}` forms; `ParseModelId` is `public static` so tests can call it directly. GraphQL client uses raw `HttpClient` (named, 15 s timeout, User-Agent header) — no StrawberryShake. `PrintablesApiException` separates upstream errors (→ 502) from bad-URL parse errors (→ 400). DI in `SlicerApiExtensions.AddSlicerApiServices` via `AddHttpClient<PrintablesGraphQLClient>` + `AddScoped<IPrintablesImportService>`. Tests: URL parsing (Theory), mocked HttpMessageHandler for GraphQL client, Moq for controller outcomes — 18 tests, all green.

- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "bambuddy", "maziggy", "Bambu Buddy", github.com/maziggy/bambuddy. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.
- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "external-reference-app", "external-author", "external reference app", [external reference repo]. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.

- **2026-05-31 — SmartPlug provider pattern (PR #370).** `ISmartPlugProvider` lives in `src/api/Services/SmartPlug/`. Register all providers as `IEnumerable<ISmartPlugProvider>` singletons in `ServiceCollectionExtensions.RegisterSmartPlugProviders()`. Kasa uses raw TCP (port 9999, XOR obfuscation) — no `IHttpClientFactory` needed. Tasmota, Shelly, HA share the named `SmartPlug` HttpClient (5s timeout). Shelly auto-detects Gen 1 (`/meter/0`) vs Gen 2 (`/rpc/Switch.GetStatus`) by trying Gen 2 first. HA device address format: `{baseUrl}|{entityId}`; token in `HomeAssistant:Token` config key (env `PFARM__HomeAssistant__Token`). No DB entities in this PR — `PowerReading` is a plain record; entities + migrations in #346.

- **2026-05-31 — Artifact metadata endpoint pattern (#336, PR #365).** Added `GET /api/artifacts/{id}/metadata` to slicer-host `ArtifactsController`. Pattern: (1) load artifact, (2) load parent `SliceJob`, (3) compare `job.UserId` vs caller's `ClaimTypes.NameIdentifier` claim — admin bypass via `User.IsInRole("farm_admin")`. `downloadUrl` is hardcoded to `/api/artifacts/{id}` — same as the existing binary download action. DTO is a C# `record` in `Farm.Slicer.Module/Dtos/`. `[ProducesResponseType]` attributes cover 200/404/403. Tests use `ControllerContext` with a `DefaultHttpContext` carrying a `ClaimsPrincipal` for auth-sensitive unit tests — no need to spin up a full HTTP pipeline.

- **2026-05-31 — beads (`bd`) not available in worktrees.** Running `bd` from a worktree directory (e.g. `/Users/jpapiez/s/PFarm1-336`) fails with "no beads database found". The `.beads/` directory lives only in the main tree. Workaround: `BEADS_DIR=/path/to/main-tree/.beads bd ...`, or run `bd` from the main tree path. If `.beads/` is absent from the main tree entirely, the database has not been initialized — skip `bd sync` step and note as a blocker in the health report.

- **2026-05-31 — Spoolman filament cost provider (#342, PR #378).** `IFilamentCostProvider` is the abstraction; `SpoolmanFilamentCostProvider` is the Spoolman-backed implementation. Lives in `src/infra/Services/Cost/`. Uses `IMemoryCache` (5-min TTL, `spoolman_cpg_spool_{id}` / `spoolman_cpg_filament_{id}` keys). Registered as Scoped (not Singleton) to avoid captive-dependency with `ISpoolmanService` typed HttpClient. Optional ctor injection `IFilamentCostProvider? filamentCostProvider = null` follows same pattern as `IJobCostCalculationService?` in `PrintJobCompletionService`. All exceptions caught → `null` return; Spoolman unconfigured also returns `null` (BaseUrl empty check inside `ISpoolmanService`). Multi-spool cost path in `JobCostCalculationService` uses provider as fast path; falls back to settings cascade on null. Cost per gram = Price / InitialWeightG (spool), or Price / Weight (filament).
- **2026-06-01T15:18:38-07:00 — System info API pattern (#435).** `GET /api/system/info` lives in `src/api/Controllers/SystemInfoController.cs`, delegates to `Farm.Infrastructure.Services.SystemStatus.ISystemInfoService`, and returns DTOs from `src/infra/Dtos/SystemInfoDtos.cs`. Host metrics pattern: CPU = `/proc/stat` on Linux or `GetSystemTimes` on Windows; memory = `/proc/meminfo` on Linux or `GlobalMemoryStatusEx` on Windows with `Process.WorkingSet64` fallback; disk/archive sizes come from `IStoragePathService.GetGcodeStorageDirectory()`, DB engine/version/size come from `AppDbContext.Database.ProviderName` + provider-specific scalar queries. Frontend contract lives in `src/Web/ReactApp/src/types/api.ts` + `src/Web/ReactApp/src/services/api.ts`; auth/shape coverage is in `src/tests/Farm.Web.Api.Tests/Integration/SystemInfoIntegrationTests.cs`.
- **2026-06-02T06:49:25.421-07:00 — Printables selected-file import pattern.** `POST /api/3d-models/import/printables` now accepts `fileIds` on `src/slicer/Farm.Slicer.Module/Dtos/PrintablesImportRequest.cs`; the controller stays transport-only in `src/slicer/Farm.Slicer.Module.Api/Controllers/PrintablesImportController.cs` and delegates import selection to `src/slicer/Farm.Slicer.Module/Services/PrintablesImportService.cs`. Service rule: null/empty `fileIds` imports all previewed STL files, unknown IDs throw `ArgumentException` for controller-mapped 400s, and actual file import resolves temporary download links via `PrintablesGraphQLClient.GetStlDownloadUrlAsync`, then pipes each file through `IModel3DFileService.UploadModelAsync` + `SetAttributionAsync`. 
- **2026-06-03T10:26:17.641-07:00 — Refit certificate upgrade pattern (#497).** Live Refit package references are split between root `Directory.Build.props` (`Refit`) and project-level `Refit.HttpClientFactory` references in `src/backends/Directory.Build.props`, `src/api/Farm.Web.Api.csproj`, `src/infra/Farm.Infrastructure.csproj`, `src/printer-discovery/PrinterDiscoveryService.csproj`, `src/slicer/Farm.Slicer.Module/Farm.Slicer.Module.csproj`, and the two main migration projects. Refit 11.0.0 still ships `Refit.HttpClientFactory` as a separate package; `10.2.0` is the re-signed non-breaking fallback for revoked `10.1.6`, but latest stable is `11.0.0`. Validate from `src/` with restore/build/test, and treat current failures in `Farm.Slicer.Module.Tests` (missing filament fixture JSON) and `Farm.Web.Api.Tests` (`SlicerDbContext` registration / MMU retro-sync expectations) as pre-existing unless Refit-specific errors appear.

## 2026-05-31 — Trio Review Cycle #355, #371, #405

Participated in multi-round trio review cycle. Key learnings:

1. **Multi-reviewer consensus:** Three independent reviewers with fresh hands prevents fatigue. (The author-lockout rule that once accompanied this has been RESCINDED by the repo owner — authors fix their own rejected work; nobody is ever locked out of an artifact.)
2. **Kane surgical-fix MVP:** Small, scoped corrections across all three branches proved cost-effective.
3. **Session-end report validation:** Coordinator must verify trio drops match current commit SHA.
4. **PR auto-close gap:** `Closes #N` does not fire on development merges; manual close required.

### 2026-07-13 — Issue #708 Backend v3 Revision (Hicks REQUEST_CHANGES Handoff)

**Context:** Hicks completed immutable review (gpt-5.6-sol/max) on detached worktree at exact SHA `1d803b930c797aec69ade5d9a98d9635c0cdabb4`. Verdict: REQUEST_CHANGES (5 blockers).

**Blockers for this revision:**
1. APNs token redaction incomplete — slash/query chars escape masking patterns
2. JWT invalidation regression test missing — second signing not proven to invalidate prior token
3. Rate-bucket prune race + hard-coded 5m expiry — concurrent scenarios unvetted; duration not configurable
4. Attention preferences inconsistency — partial reset + toggle mismatch indicates incomplete persistence
5. Capabilities JSON casing mismatch — non-production serializer options cause camelCase/PascalCase divergence

**Verified:** B3 auth ✓, migrations ✓, build ✓, 75 focused tests ✓, full suite 3251/3253 (2 unrelated).

**Next Steps:** Fix above blockers on this branch. Jeff Papiez locked out for this revision cycle. Hicks will re-review after fixes.
