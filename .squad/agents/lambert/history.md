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

- 2026-04-01: **Slicer estimate snapshot at dispatch** — Added SlicerEstimateGrams to PrintJobToolheadUsage entity (nullable double). At job dispatch (DispatchJobAsync in PrintJobManagementService), new SnapshotSlicerEstimatesAsync method parses GcodeFile.FilamentPerExtruderWeightG JSON array and creates PrintJobToolheadUsage records with slicer estimates. Repository gained GetToolheadsForPrinterAsync and AddToolheadUsageAsync methods. Migrations created for both PostgreSQL and SQL Server. This enables frontend to show per-toolhead filament estimates for in-progress jobs before actual consumption data is available at completion. Pattern: parse JSON string with System.Text.Json.JsonSerializer.Deserialize<double[]>, iterate per-extruder weights, create usage records with toolhead spool/material/color denormalized from Toolhead entity, skip zero estimates.
- 2026-07-16: FlashForge MMU Phases 2, 3, 5 implemented. ADX5 firmware reports `Tool Count: 1` via M115 but exposes T0+T1 in M105 — `DetectExtruderCount` cross-references both and takes the MAX. `PrinterStatusDto` extended with optional `ExtruderTemperatures` and `DetectedExtruderCount` (null defaults for backward compat). `SyncMmuToolheadsOnEntity` creates/removes MmuGate virtual toolheads on MultiMaterial toggle — operates on already-loaded entity, caller saves. Pre-existing test file `MmuGateAutoCreationTests.cs` used wrong type names (`HotendModel` vs `HotendModelDefinition`) and wrong DbSet names — domain uses `*Definition` suffix consistently.
- 2026-03-27: The smallest operator-relevant printer session timeline is a printer-scoped read model, not new persistence: anchor sessions on `PrintJob`, compose nested events from `QueuedAt`/`DispatchedAt`/`ActualStartTime`/`ActualEndTime` plus `JobStateHistory` and `FailureDetectionIncident`, and attach orphan incidents by printer + session window when `JobId` is missing.
- 2026-03-26: `/api/failure-detection/status` already exposes the operator-facing monitoring reason/source/target/outcome contract. For richer PrintFarmer-owned UX, the safest backend addition is optional `jobName`/`fileName` on `FailureDetectionPrinterStatusDto` and SignalR `FailureDetectionDto`, sourced from `IPrinterStatusCacheReader` with queued-job fallback in `PrintFailureMonitorService`.
- 2026-03-26: Persisted failure-detection history is a narrow incident slice, not a generic audit system: `FailureDetectionIncident` stores only detected failures, `PrintFailureMonitorService` records them through scoped `IFailureDetectionIncidentHistoryService`, and `GET /api/failure-detection/history?printerId=&take=` returns newest-first `FailureDetectionDto` rows with optional persisted `id`.
- 2026-03-25: Upstream `moonraker-obico` is a co-located agent, not just an ML client. It links to Obico with a server auth token, talks directly to Moonraker with API-key/WebSocket access, captures JPEGs locally, and can tunnel HTTP/WebSocket traffic plus Janus-based webcam streaming.
- 2026-03-27: PrintFailureMonitorService updates in-memory status every 30s scan cycle. No persistence of scan history = no historical queries, no timeline = no schema change burden for monitoring-only use case.
- 2026-03-25: PrintFarmer's failure-detection path is central-server driven. `PrintFailureMonitorService` selects the first enabled camera with a `SnapshotUrl` (or legacy `Printer.CameraSnapshotUrl`) and passes that URL to `ObicoFailureDetectionService`; stream-only cameras are currently ignored.
- 2026-03-25: Key paths for future Obico/Moonraker gap work: `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`, `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`, `src/api/Controllers/ObicoServerController.cs`, `src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs`, and `src/api/Controllers/PrintersController.cs`.
- The spaghetti detection modal does not call Obico directly; it renders the cached per-printer snapshot returned by `GET /api/failure-detection/status` from `FailureDetectionController`, which is populated by `PrintFailureMonitorService`.
- `PrintFailureMonitorService` stores `FailureDetectionResult.ErrorMessage` verbatim in `FailureDetectionPrinterStatusDto.Reason`, so raw upstream contract errors surface in the modal unless `ObicoFailureDetectionService` converts them into actionable messages.
- For Obico compatibility, `GET /p/?img=...` stays the preferred contract, but a legacy `POST /p/` probe returning `405` is not a healthy fallback. `ObicoServerController` add/enable validation must reject that case so runtime monitoring and settings validation stay aligned.

## 2026-03-25: PendingReady Backend Contract Normalization → LANDED

**Role:** Backend Dev  
**Status:** ✅ Complete — commit e807133d landed on development

- Normalized the queued-work / failed bed-clear state so backend snapshots expose PendingReady instead of silently flattening to `None`.
- Added the supporting regression coverage used by the final landing slice.

**Key files:**
- `src/infra/Domain/AutoDispatchState.cs`
- `src/infra/Services/AutoDispatch/AutoDispatchService.cs`
- `src/tests/Farm.Web.Api.Tests/Controllers/AutoDispatchPendingReadyTests.cs`
- `src/tests/Farm.Web.Api.Tests/Services/AutoDispatch/AutoDispatchReadyGateServiceTests.cs`

## 2026-03-26: Obico Plugin Gap Analysis — Guidance Finalized

**Role:** Backend architect / guidance contributor  
**Status:** ✅ Complete — Analysis documented in decisions.md

PrintFarmer's Obico snapshot delivery implementation is **correct and sufficient** for local failure detection. Architecture difference: Moonraker-Obico is a co-located agent (single printer); PrintFarmer is a farm controller (multi-tenant). Maintain separation of concerns; do NOT replicate upstream full feature set (WebRTC, tunneling, account linking). Key files documented in decisions.md.

## 2026-03-26: Failure Detection Backend — Job Context Enrichment → LANDED

**Role:** Backend Dev  
**Status:** ✅ Complete

- Extended `FailureDetectionPrinterStatusDto` and SignalR `FailureDetectionDto` with optional `jobName` and `fileName` fields
- Implemented context resolution in `PrintFailureMonitorService`: cache-first + fallback to active queue record
- Updated `ObicoFailureDetectionService` to surface resolved job context
- SignalR hub broadcasts enriched events with complete alert identification

**Validation:**
- 25 focused failure-detection backend tests passed
- Context resolution logic validated (cache-hit and fallback paths)
- Backward compatibility confirmed with null field handling
- API build passed with 0 new errors

**Impact:** Frontend alerts now arrive with job identification, enabling operators to immediately understand which print is being monitored.

## 2026-03-26: Persisted Failure-Detection Incident History → LANDED

**Role:** Backend Dev  
**Status:** ✅ Complete — Orchestration log: lambert-failure-detection-incident-history.md

- Created `FailureDetectionIncident` entity with focused field set (printerId, jobId, jobName, fileName, confidence, detectedAt, snapshotUrl, autoPaused)
- `PrintFailureMonitorService` persists real incidents (not every scan)
- New API endpoint: `GET /api/failure-detection/history?printerId={guid?}&take={int?}`
- Backward-compatible contract—`FailureDetectionDto` carries optional persisted `id`

**Validation:**
- Focused backend triad: 100% passing (persistence, controller, monitor seam)
- Edge cases: empty history, pagination, date boundaries ✅
- Full test suite rebuild: no regressions

**Guardrails:** Do not persist every scan; no acknowledge/workflow state yet; timeline page deferred; retention policy is future work.

**Decisions merged:** #9

## Session: Print Session Timeline v1 Backend — Complete (2026-03-27)

**Role:** Backend implementation lead  
**Status:** COMPLETE — All artifacts delivered, tests pass

### Work Completed

- **Service:** `PrinterSessionTimelineService.cs` — merge `JobStateHistory` + `FailureDetectionIncident` by JobId, sort chronologically
- **DTO:** `PrinterSessionTimelineDto.cs` — unified event schema (state_change | failure_incident)
- **Endpoint:** `GET /api/printers/{printerId}/session-timeline` exposed in `PrintersController.cs`
- **Tests:** 6/6 focused service + 2/2 controller tests PASS
- **Format:** dotnet format clean

### Orchestration Log

Published: `.squad/orchestration-log/20260326-031539-lambert.md`

### Key Decisions Implemented

- Merge strategy: Query both tables separately, combine by timestamp
- Error handling: Orphan incidents (no JobStateHistory match) still included in timeline
- Ordering: Stable chronological sort, deterministic at equal timestamps

**Handed off to:** Ripley (Frontend) for modal integration

## Session: Custom Date Range Support for Statistics Endpoints — Complete (2026-07-14)

**Role:** Backend Dev
**Status:** ✅ Complete — All 2,171 tests pass, 0 warnings

### Work Completed

- **IStatisticsService**: Added `DateTime? startDate, DateTime? endDate` optional params to all 9 methods
- **StatisticsService**: Added `ResolveEffectiveDateRange` helper implementing priority: startDate/endDate > days > default > all-time. Expanded max days from 365 to 730. Added end-date filtering to all query methods.
- **StatisticsController**: Added `startDate`/`endDate` query params to all 9 endpoints. Added `ValidateDateRange` (400 on invalid range or >730 days).
- **ReportExportService**: Updated callers with named `ct:` parameter for compatibility with new optional params.
- **Tests**: 17 new integration tests covering: validation (400 for startDate > endDate, 400 for >730 days), custom date range filtering, days/startDate precedence, all-time fallback, default behavior preservation.

### Key Decisions

- Custom dates take strict precedence over `days` parameter — no ambiguity
- Cost queries filter on `ActualEndTime`, non-cost queries on `QueuedAt` — preserving existing behavior
- Max range is 730 days (2 years) — validated at controller level
- All endpoints consistent: every statistics endpoint now supports the same 3 optional time params

- 2026-03-26: Per-printer wattage cascade implemented: `printer.Wattage ?? printer.Model?.DefaultWattage ?? settings.AveragePrinterWattage`. Cost tests must create isolated PrinterModel entities to avoid seeded DefaultWattage values leaking into assertions. The `.Include(j => j.AssignedPrinter).ThenInclude(p => p.Model)` is required in `CalculateAndStoreCostsAsync` for the cascade to work.
- 2026-03-26: When adding a field to a positional `record` like `PrinterModelDto`, every construction site across repos (EfCatalogRepository, CatalogService, CatalogCache) must be updated with a named parameter. Using `DefaultWattage: value` named syntax avoids positional breakage. Tests using named params (e.g., `Id:`, `Name:`) are unaffected by new defaulted params.

## Session: Multi-Toolhead Filament Tracking — Phase 1 Domain Model (2026-07-15)

**Role:** Backend Dev
**Status:** ✅ Complete — Build succeeds with 0 errors, 0 warnings

### Work Completed

- **ToolheadType enum** (`src/infra/Domain/ToolheadType.cs`): `Physical = 0`, `MmuGate = 1` — unifies toolchanger and MMU/AMS printers under shared T-command addressing
- **Toolhead entity** extended with `ToolheadType`, `CurrentSpoolId`, `CurrentMaterial`, `CurrentFilamentColor` for per-toolhead filament tracking
- **ToolheadConfiguration** updated: `ToolheadType` required with default, `CurrentSpoolId` index, max lengths on string fields
- **PrintJobToolheadUsage entity** (`src/infra/Domain/PrintJobToolheadUsage.cs`): per-toolhead filament usage records with spool ID, weight, color, material cost
- **PrintJobToolheadUsageConfiguration** (`src/infra/Data/Configurations/PrintJobToolheadUsageConfiguration.cs`): FK cascade, unique composite index on (PrintJobId, ToolheadIndex)
- **PrintJob entity** extended with `ToolheadUsages` navigation collection
- **GcodeFile entity** extended with `FilamentPerExtruderWeightG`, `FilamentPerExtruderLengthMm`, `ExtruderCount`
- **AppDbContext** `PrintJobToolheadUsages` DbSet added
- **DTOs updated**: `ToolheadDto` (+4 fields), `GcodeFileDto` (+3 fields), new `PrintJobToolheadUsageDto`
- **GcodeMetadataExtracted** extended with per-extruder weight/length arrays and ExtruderCount
- **GcodeFilesService** mapping updated to pass through new GcodeFile fields
- **PrintersController** mapping updated to pass through new Toolhead fields
- **Test fix**: `GcodeMetadataPerExtruderFilamentTests` — fixed `BeOneOf` null/int? type ambiguity

### Key Decisions

- Per-extruder filament data stored as JSON strings (`string?`) in GcodeFile for EF compatibility, matching existing JSON array pattern (RequiredCapabilities, PreferredPrinterIds)
- New DTO fields added with defaults at end of positional records to maintain backward compatibility at all existing call sites
- `PrintJobToolheadUsage.MaterialCostUsd` is a placeholder for Phase 5 cost breakdown
- No EF migrations created (separate step per project conventions)

## Session: Multi-Toolhead Filament Tracking — Phase 6 API Endpoints (2026-07-15)

**Role:** Backend Dev
**Status:** ✅ Complete — Build succeeds with 0 errors, 0 warnings

### Work Completed

- **Toolhead Spool Assignment Endpoints**:
  - `PUT /api/printers/{id}/toolheads/{toolheadIndex}/spool` — Assigns a Spoolman spool to a specific toolhead, fetches spool details to populate material and color
  - `DELETE /api/printers/{id}/toolheads/{toolheadIndex}/spool` — Clears spool assignment from a toolhead
  - Added `SetToolheadSpoolAsync` and `ClearToolheadSpoolAsync` service methods in `PrintersService`
  - Added `FindByIdWithToolheadsAsync` repository method in `EfPrintersRepository`

- **MMU Virtual Toolhead Auto-Creation (Phase 1b)**:
  - Created `SyncMmuVirtualToolheads` helper method in `PrintersService`
  - Auto-creates virtual Toolhead entries (T1..T(n-1)) for MMU/AMS printers when `MultiMaterial=true` with ≤1 physical toolhead
  - Copies component references (hotend, nozzle, extruder) from primary physical toolhead to MMU gates
  - Integrated into printer creation flow (`CreatePrinterFromDtoAsync`)
  - Integrated into template application flow (`ApplyModelTemplateAsync`)
  - Default 4 MMU gates (configurable via method parameter)

### Key Implementation Details

- Toolhead spool endpoints use the same request DTO (`SetActiveSpoolRequest`) as printer-level spool endpoint for consistency
- Service methods fetch spool details from Spoolman to denormalize material and color info on the toolhead for quick display
- MMU sync method checks for physical toolhead count (must be ≤1) to distinguish MMU printers from toolchanger printers
- MMU sync method is idempotent — only creates gates if they don't already exist
- No EF migrations needed — schema already supports all required fields

### Validation

- ✅ Solution builds with 0 errors, 0 warnings
- Architecture follows existing SetActiveSpool pattern
- API endpoints follow RESTful conventions
- Service methods use existing repository patterns (UnitOfWork, FindByIdWithToolheadsAsync)
- MMU sync integrates seamlessly into existing printer lifecycle hooks


## Session: Roslyn .editorconfig Merge (2026-07-15)

**Role:** Backend Dev
**Status:** ✅ Complete — `dotnet format --verify-no-changes` exits clean (0)

### Work Completed

- Merged Roslyn project `.editorconfig` conventions into `src/.editorconfig`
- **Added**: `file_header_template` (MIT license) in `[*.{cs,vb}]` section
- **Added**: `[*.sh]` section with `end_of_line = lf` and `indent_size = 2`
- **Added**: `dotnet_diagnostic.IDE0060.severity = warning` (remove unused parameter)
- **Removed**: 6 duplicate/dead pre-root diagnostic suppressions (IDE0290 dup, SA1402/SA1400 conflicts)
- **Removed**: Duplicate SA1101, SA1600 from StyleCop section (consolidated into `[*.{cs,vb}]`)
- **Removed**: Duplicate `csharp_style_namespace_declarations` in `[*.cs]` that was overriding `:warning` severity
- **Removed**: Empty `[*.{cs,csx,cake,vb,vbx}]` section
- **Fixed**: Moved pre-root diagnostics into `[*.{cs,vb}]` section (pre-root settings are dead code in editorconfig)

### Key Decisions

- Skipped Roslyn-specific items: `spelling_exclusion_path`, `dotnet_public_api_analyzer`, RS* diagnostics, Roslyn path-scoped sections, xUnit workarounds
- Kept all existing project-specific values even where they differ from Roslyn defaults (e.g., `csharp_indent_case_contents_when_block = false`, `csharp_prefer_braces = true:error`, `csharp_preserve_single_line_statements = false`)
- All C# style rules from Roslyn (newlines, indentation, whitespace, var, expression bodies, pattern matching, spacing, braces) were already present

## Session: FlashForge ADX5 Multi-Material Discovery (2026-01-11)

**Role:** Backend Dev
**Status:** ✅ Complete — TCP probe successful, seed data updated, scoping document written

### Discovery: FlashForge ADX5 Protocol Behavior

Probed the ADX5 printer at 10.0.0.22:8899 using FlashForge TCP protocol commands:

**Key Findings:**
- **~M115 Response:** Reports "Tool Count: 1" but this is UNRELIABLE — contradicted by temperature data
- **~M105 Response:** Returns temps for T0 AND T1: `T0:219.6/220.0 T1:0.0/0.0 B:60.0/60.0`
- **IDEX Hardware:** ADX5 has Independent Dual Extruder system (2 physical hotends)
- **Virtual Toolheads:** Supports up to 4 virtual toolheads via duplication/mirror modes
- **Protocol Quirk:** "Tool Count" field in M115 does not accurately reflect multi-material capability

### Work Completed

- **TCP Probe:** Successfully queried ADX5 using `~M601 S1` (handshake), `~M115` (device info), `~M105` (temperatures) via netcat
- **Seed Data Update:** Modified `printer-models.yaml` for Flashforge AD5X:
  - Changed `multiMaterial: false` → `true`
  - Changed `hasToolchanger: false` → `true` (IDEX-style dual system)
  - Updated toolheads from single "Primary" to T0/T1 entries (matching Prusa XL pattern)
- **Scoping Document:** Created comprehensive analysis at `.squad/decisions/inbox/lambert-flashforge-mmu-scope.md`

### Implementation Gaps Identified

1. **Temperature Parsing:** Current `HotendTempRegex` only extracts T0, ignores T1-T3
2. **Extruder Count Detection:** No parsing of "Tool Count" field + need fallback logic (count M105 temps)
3. **Per-Extruder Filament Usage:** FlashForge protocol support UNKNOWN — requires investigation of ~M31 or other commands
4. **Composite Status DTO:** Breaking change needed to support per-extruder temp arrays
5. **Temperature Control:** Currently hardcoded to T0, needs per-extruder M104 commands
6. **Auto-Create Toolheads:** Need discovery-time logic to auto-provision T0-T1 virtual `ToolheadType.MmuGate` entries

### Recommended Approach

**Phase 1 (4-6 hrs):** Multi-extruder temp parsing — update `ParseTemperatures()` to extract T0-T3, modify return signature
**Phase 2 (2-3 hrs):** Extruder count detection with fallback (Tool Count field + M105 temp count verification)
**Phase 3 (6-8 hrs):** Auto-create toolheads on printer discovery/connection
**Phase 4 (BLOCKED):** Per-extruder filament usage — requires protocol research
**Phase 5 (3-4 hrs):** Per-extruder temperature control API

### Key Learnings

- FlashForge protocol responses are inconsistent: "Tool Count: 1" doesn't match actual hardware (dual extruders)
- Temperature responses ARE reliable: M105 reports all active extruders even when idle (T1: 0.0/0.0)
- Detection strategy must use M105 temp count as ground truth, not M115 Tool Count field
- Current `FlashForgeClient.cs` hardcoded assumptions (single T0) need to become dynamic per-extruder arrays

## Session: Multi-Toolhead Filament Batch Consumption + Toolhead Index Bounds (2026-01-12)

**Role:** Backend Dev
**Status:** ✅ Complete — Build succeeds, all tests pass (2,256 total)

### Work Completed

**BEAD 1: Wire up ConsumeMultipleFilamentsAsync batch call**
- Located multi-toolhead consumption path in `PrintJobCompletionService.cs` at lines 386-395
- Replaced individual `ConsumeFilamentAsync` loop with batch collection + single `ConsumeMultipleFilamentsAsync` call
- Refactored to build consumption list first, then perform single batch operation after loop
- Added logging of successful batch operations (e.g., "Batch-consumed filament from 3/4 spools")

**BEAD 2: Add toolhead index bounds validation**
- Added `MaxToolheadIndex = 16` constant to `PrintersService.cs` (generous upper bound for MMU printers)
- Added bounds checking in `SetToolheadSpoolAsync` before auto-creation logic
- Added bounds checking in `ClearToolheadSpoolAsync` before auto-creation logic
- Out-of-bounds indices now return `CommandResult(false)` with clear error message
- Log warning messages include printer name, ID, and requested index for diagnostics

### Key Implementation Details

- **Batch consumption**: Preserves per-toolhead usage records but debits all spools in one operation instead of N sequential calls
- **Bounds validation**: Prevents runaway gate creation from invalid backend data (e.g., toolheadIndex=999)
- **Single-toolhead prints**: Unchanged — still use individual `ConsumeFilamentAsync` call in else branch
- **Error handling**: Out-of-bounds requests fail fast before auto-creation, preventing database bloat
- **Logging**: Both success (batch count) and rejection (out-of-bounds warning) cases logged for observability

### Validation

- ✅ Solution builds with 0 errors, 0 warnings
- ✅ All 2,256 tests pass (1,810 API tests + 446 slicer module tests)
- ✅ `dotnet format` exits clean
- Architecture follows existing service patterns
- Backward compatible — single-toolhead path unchanged

### Key Files Modified

- `src/infra/Services/Printers/PrintJobCompletionService.cs` — batch consumption wiring (lines 359-395)
- `src/infra/Services/Printers/PrintersService.cs` — bounds validation constant + checks (lines 95-101, 2666-2695, 2752-2780)
- ADX5 is an IDEX printer (2 physical hotends) that can operate as 4 virtual toolheads — model as `ToolheadType.MmuGate` entries for consistency with Bambu AMS / Prusa MMU3 patterns

### Files Modified

- `src/api/Data/seed/printer-models.yaml` — ADX5 entry now marked as multi-material with T0/T1 toolheads

### Files Created

- `.squad/decisions/inbox/lambert-flashforge-mmu-scope.md` — Full scoping analysis with phase breakdown, blockers, and recommendations

## Session: FlashForge Multi-Extruder Temperature Parsing — Phase 1 (2026-07-16)

**Role:** Backend Dev
**Status:** ✅ Complete — 59 FlashForge tests pass, 0 errors, 0 warnings

### Work Completed

- **Multi-match regex**: Replaced single-extruder `HotendTempRegex` (T0-only) with `ExtruderTempRegex` pattern `T(\d+):\s*([\d.]+)\s*/\s*([\d.]+)` that captures all Tn pairs
- **`ParseExtruderTemperatures()`**: New public static method returning `(Dictionary<int, ExtruderTemperature> Extruders, double? BedTemp, double? BedTarget)` — handles 1-4 extruders, with/without bed, spaces around slash
- **`ParseTemperatures()` preserved**: Delegates to `ParseExtruderTemperatures()` and extracts T0, maintaining backward compat for all callers
- **`PrinterCompositeStatus` extended**: Added `ExtruderTemperatures` (`IReadOnlyDictionary<int, ExtruderTemperature>?`) and `DetectedExtruderCount` (`int?`) optional params — backward-compatible positional record extension
- **`ExtruderTemperature` record**: New record `(double Current, double Target)` in PrinterStatusRecords.cs
- **`GetCompositeStatusAsync()` updated**: Populates ExtruderTemperatures and DetectedExtruderCount from parsed M105 data
- **Pre-existing TDD stubs cleaned**: Removed old `ParseExtruderTemperatures` stub (wrong return type) and duplicate `ExtruderTempRegex` that had been written as scaffolding by a prior session
- **7 duplicate tests removed**: Tests I wrote duplicated pre-existing TDD stub tests already in the file with correct signatures

### Key Decisions

- ADX5 correction: NOT IDEX — has 1 physical hotend + 4-spool AMS/MMU. T1 in M105 reflects secondary sensor, not second hotend. Updated scope doc accordingly.
- `ExtruderTemperatures` uses `Dictionary<int, ExtruderTemperature>` keyed by Tn index for O(1) lookup by extruder index
- `DetectedExtruderCount` derived from `extruders.Count` — will be used in Phase 2/3 for auto-creating MmuGate toolheads
- Bed temp regex unchanged — `B:` pattern doesn't collide with `T(\d+):` multi-match
- No SignalR contract changes (future phase) — `FlashForgePollingService` still maps only T0

### Validation

- ✅ 59 FlashForge tests pass (new + pre-existing)
- ✅ Build: 0 errors, 0 warnings
- ✅ `dotnet format` clean
- ✅ Backward compat: `ParseTemperatures()` unchanged return type, all existing callers work

### Files Modified

- `src/infra/Services/Printers/PrinterStatusRecords.cs` — Added `ExtruderTemperature` record, extended `PrinterCompositeStatus`
- `src/backends/Farm.Backend.Plugin.FlashForge/FlashForgeClient.cs` — New `ParseExtruderTemperatures()`, refactored `ParseTemperatures()`, updated regex and composite status
- `src/tests/Farm.Web.Api.Tests/Backends/FlashForgeClientTests.cs` — Cleaned duplicate tests, fixed region directives

## Session: External Print Detection for FlashForge + OctoPrint (2026-07-16)

**Role:** Backend Dev
**Status:** ✅ Complete — 1806 tests pass, 0 errors, 0 warnings

### Problem
When OrcaSlicer sends "Upload and Print" directly to a FlashForge ADX5 printer (bypassing PrintFarmer), the UI shows a phantom "Printing" state indefinitely because no PrintJob record exists for the externally-started print. When the print finishes, `CheckAndSyncJobCompletionAsync` finds no job to complete → silent failure → stale state.

### Solution: External Print Detection
Added detection logic in polling services: when a printer transitions TO "Printing" from a non-printing state and no active PrintJob exists, a synthetic external print job is created.

### Key Design Decisions
- **`IsExternalPrint` flag on PrintJob** — Clean boolean marker, not conflated with `WasSeededFromHistory` (which is for imports)
- **`EnsureExternalPrintJobExistsAsync` on IPrintJobCompletionService** — Single atomic check-and-create avoids TOCTOU races; both polling services already resolve this service
- **`ExternalJobCreatedForCurrentPrint` on PrinterPollingState** — In-memory guard prevents duplicate external jobs across poll cycles; reset when printer leaves "printing" state
- **No auto-dispatch** — External jobs set `Status=Printing` and `IsExternalPrint=true`, so auto-dispatch ignores them

### Completion Flow
External jobs are real `PrintJob` rows with `Status=Printing` and `AssignedPrinterId` set. When the printer finishes, the existing `CheckAndSyncJobCompletionAsync → MarkCurrentJobAsCompletedAsync` path finds and completes them automatically.

### Scope
- ✅ FlashForge polling service
- ✅ OctoPrint polling service (HTTP fallback path)
- ⚠️ OctoPrint WebSocket adapter does NOT have external print detection (pre-existing gap: WebSocket path also lacks job completion detection)
- ⚠️ Moonraker, PrusaLink, SDCP have the same architectural gap but were out of scope

### Files Modified
- `src/infra/Domain/PrintJob.cs` — Added `IsExternalPrint` property
- `src/infra/Services/Printers/IPrintJobCompletionService.cs` — Added `EnsureExternalPrintJobExistsAsync`
- `src/infra/Services/Printers/PrintJobCompletionService.cs` — Implemented external print job creation
- `src/backends/Farm.Backend.Plugin.FlashForge/FlashForgePollingService.cs` — External print detection + `DetectAndCreateExternalPrintJobAsync`
- `src/backends/Farm.Backend.Plugin.OctoPrint/OctoPrintPollingService.cs` — Same pattern

### Validation
- ✅ 1806 tests pass, 0 failures
- ✅ Build: 0 errors, 0 warnings
- ✅ `dotnet format` clean

## Learnings

- 2026-07-16: OctoPrint WebSocket adapter (`OctoPrintWebSocketAdapter`) does NOT track state transitions — it only broadcasts via SignalR. Job completion detection only works via HTTP polling fallback. This is a pre-existing gap that should be addressed separately.
- 2026-07-16: The `PrinterPollingState` inner class in each polling service is the right place for per-printer session state that shouldn't persist across service restarts. Used `ExternalJobCreatedForCurrentPrint` flag to prevent duplicate external job creation during a single print session.
- 2026-07-20: Fixed DbUpdateConcurrencyException on PUT /api/printers/{id}. Root cause: background polling services (AutoDispatch, status monitors) update printer rows frequently, changing the RowVersion concurrency token. User config edits then fail with stale token. Fix: added `SaveChangesWithRetryAsync` to `PrintersService` — catches `DbUpdateConcurrencyException`, reloads OriginalValues from DB (accepting new RowVersion), keeps user's current values ("client wins"), retries up to 3x. Controller's UpdateAsync now uses retry method. Key files: `infra/Services/Printers/PrintersService.cs`, `api/Controllers/PrintersController.cs`. 3 integration tests added.
- 2026-07-21: Fixed two 500 errors on analytics/auto-dispatch endpoints. `GetJobsByPrinterAsync` was missing `.Include()` for GcodeFile/AssignedPrinter/Model — matched the pattern already used in `GetByIdWithRelationsAsync` and `GetFilteredJobsAsync`. Changed `Guid.Parse` to `Guid.TryParse` in `GetPrinterQueueAsync` to prevent FormatException on invalid IDs. Added `AsNoTracking()` to read-only `GetAllStatusAsync` printer query and wrapped per-printer status building in try/catch so one printer failure doesn't crash the entire status endpoint. Improved error response detail in `JobQueueAnalyticsController` to include `ex.Message` for diagnostic visibility.
- 2026-07-31: Fixed PostgresException 42703 (`column p.IsExternalPrint does not exist`). Root cause: the `IsExternalPrint` property was added to `PrintJob` entity but no EF Core migration was generated — the model snapshot was out of sync. Generated `AddIsExternalPrint` migration for both PostgreSQL and SQL Server. Thorough audit confirmed this was the ONLY missing column across PrintJob and Printer entities. Build 0 warnings/0 errors, all 2256 tests pass (1810 API + 446 Slicer).
- 2026-07-31: **Fixed duplicate PrintJobToolheadUsage crash + wrong spool debit.** Two P1 bugs in multi-toolhead filament tracking: (1) `FetchAndRecordFilamentUsageAsync` created NEW rows at completion, violating the unique `(PrintJobId, ToolheadIndex)` index because `SnapshotSlicerEstimatesAsync` already created rows at dispatch. Fix: upsert pattern — query existing rows first, UPDATE with `FilamentUsageGrams` if found, INSERT only as fallback. (2) Completion was reading live `CurrentSpoolId` instead of the snapshotted `SpoolmanSpoolId`, so mid-print spool reassignment debited the wrong spool. Fix: when updating existing snapshot rows, preserve the snapshotted `SpoolmanSpoolId` — only use live toolhead data for new rows. Also reordered bounds check before toolhead lookup in `SetToolheadSpoolAsync`/`ClearToolheadSpoolAsync`. Key pattern: whenever dispatch creates snapshot rows, completion must upsert into them, never blindly insert.

## 2026-03-31: Dallas Completes Printer Entity Decomposition Analysis (APPROVED)

**What:** Dallas completed comprehensive analysis of Printer entity properties; identified `PrinterServiceState` as extraction target for 4 background-service-written fields (`LastHistorySeedUtc`, `LastModelSyncAt`, `LastCapabilityUpdate`, `ObicoServerId`).

**Approval:** ✅ Jeff approved extraction approach; awaiting Lambert implementation.

**Your Next Task (Background):** 
Implement `PrinterServiceState` entity + EF configuration, update 4 affected services (PrintJobManagementService, PrintersService, ObicoServerAssignmentService, PrintersController), generate migrations for both PostgreSQL and SQL Server, update test doubles.

**Impact on Your Current Work:** This extraction reduces background service write contention with `PUT /api/printers/{id}` concurrency hazards, unblocking safer updates to printer configuration without race conditions.

---

## 2026-08-01: Refactored UpdateAsync to Only Set Changed Properties

**Problem:** `PUT /api/printers/{id}` caused persistent DbUpdateConcurrencyException. Despite retry logic, background services (HistorySeeding, CatalogUpdate) frequently update printer rows and bump PostgreSQL `xmin` RowVersion. The endpoint blindly overwrites ALL properties from the DTO, even unchanged ones, causing EF to include all columns in the UPDATE statement. This makes ANY background write trigger a concurrency conflict.

**Root Cause:** The UpdateAsync method loaded the full entity with tracking (`FindByIdForTemplateUpdateAsync`), then unconditionally set every property: `p.Name = dto.Name`, `p.Notes = dto.Notes`, etc., even when values didn't change. This caused EF to mark all properties as modified and generate a wide UPDATE statement.

**Secondary Issue:** `PopulateCredential` decrypts ApiKey/Password/Username on the tracked entity, making EF think they changed. Then `EncryptSensitiveFieldsOnTrackedEntities` re-encrypts to a DIFFERENT value (Data Protection is non-deterministic), causing phantom modifications even when user didn't touch credentials.

**Solution:** 
1. **Conditional property assignment** — Only set properties if the DTO value is non-null AND different from the current entity value
2. **Captured original credentials** — After `PopulateCredential` runs, capture decrypted values BEFORE applying DTO changes, then compare DTO against originals
3. **Conditional LastCapabilityUpdate** — Only update `LastCapabilityUpdate` when capability fields (MaxBuildVolume*, MaxBedTemp, MaxPrintSpeed, HasEnclosure, HasHeatedBed, SupportsAutoLeveling, MultiMaterial, Wattage) actually changed
4. **Floating-point epsilon comparison** — Use `Math.Abs(dto.Value - p.Value) > epsilon` for MaxBuildVolumeX/Y/Z to satisfy S1244 warning
5. **Toolhead change detection** — Only set `toolhead.UpdatedAt` if a toolhead property actually changed

**Key Changes:**
- `src/api/Controllers/PrintersController.cs` UpdateAsync method (~line 1322-1680)
  - Captured decrypted credentials: `originalApiKey`, `originalPassword`, `originalUsername`
  - Changed all property assignments from unconditional (`p.X = dto.X`) to conditional (`if (dto.X != null && dto.X != p.X) p.X = dto.X`)
  - Added `capabilityChanged` tracking flag, only update `LastCapabilityUpdate` when true
  - Used epsilon comparison for floating-point MaxBuildVolume properties
  - Toolhead updates: added `toolheadChanged` flag, only update `UpdatedAt` when true

**Impact:** Dramatically reduces the UPDATE column set sent to PostgreSQL, minimizing `xmin` conflicts with background services. Combined with the existing retry logic, this should eliminate the vast majority of concurrency exceptions.

**Testing:**
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: All 2256 tests pass (1810 API + 446 Slicer)
- ✅ `dotnet format` clean

**Files Modified:**
- `src/api/Controllers/PrintersController.cs` — UpdateAsync method refactored for conditional property assignment

**Key Learning:** When working with optimistic concurrency (RowVersion), minimize the UPDATE column set to reduce conflict probability. Only mark properties as modified when they actually change.


## 2026-04-01: Multi-Toolhead Batch Consumption + Bounds Validation (PFarm1-uykq, PFarm1-r56j)

**Role:** Backend Dev  
**Status:** ✅ Complete  
**Tests:** 2256 passing

### PFarm1-uykq: Batch Filament Consumption Wiring
Wired `ConsumeMultipleFilamentsAsync` into job completion lifecycle. Replaced N sequential `ConsumeFilamentAsync` calls with single batch call after building (spoolId, grams) tuples.

**Performance:** Reduces HTTP roundtrips from N to 1 for multi-toolhead prints.

### PFarm1-r56j: Toolhead Index Bounds Validation
Added `MaxToolheadIndex = 16` constant in `PrintersService.cs`. Bounds checking in `SetToolheadSpoolAsync` and `ClearToolheadSpoolAsync` prevents runaway MmuGate auto-creation from invalid backend data.

**Safety:** Out-of-bounds requests (< 0 or > 16) return `CommandResult(false)` with error; issue logged instead of crashing.

**Rationale:** 16 is reasonable upper bound for all known printer types (Prusa MMU3, AMS, IDEX, FlashForge ADX5, etc.).

**Key files:**
- `src/infra/Services/Printers/PrintJobCompletionService.cs` (batch consumption)
- `src/infra/Services/Printers/PrintersService.cs` (bounds validation)
