# Lambert History

## Core Context

Lambert is the backend and infrastructure architect. Key retained context:
- Owns multi-database backend changes, background services, EF migrations, and most failure-detection / Obico runtime logic.
- Key backend pattern: singleton workers resolve scoped services via `IServiceScopeFactory`, and configuration-sensitive monitors should reread persisted settings rather than assume in-memory state.
- Prefers behavior-safe adapters: add compatibility for new upstream contracts without forcing migrations for older deployments, then protect the seam with focused tests.
- Important current references: `PrintFailureMonitorService`, `ObicoFailureDetectionService`, `ObicoServerController`, and the focused Obico controller/service test files.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-07 to 2026-03-16: Delivered major backend work across auto-dispatch, analytics, camera platform prep, initial failure detection, and multi-server Obico support.
- 2026-03-25: Normalized PendingReady backend state, clarified warmup/attention boundaries, separated runtime reachability issues from route bugs, and adapted Obico to the upstream GET-first contract.
- 2026-03-26: Implemented failure-detection incident history persistence, enriched frontend alerts with job context, finalized plugin gap analysis, and validated architecture principles.

## Learnings

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
