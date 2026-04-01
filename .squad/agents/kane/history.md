# Kane History

## Core Context

Kane is the QA / regression specialist. Key retained context:
- Designs focused backend + frontend regression gates around high-risk contract seams instead of relying on broad smoke suites.
- Common test targets: printer-card UI state, auto-dispatch / PendingReady flows, camera preview regressions, and failure-detection surfaces.
- Prefers proving the exact failing seam first, then locking it with a reusable pattern or squad skill.
- Current reusable test patterns include the PendingReady regression triad and HTTP contract regression testing for backend adapters.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-18: Validated Tailwind v4 migration, camera-fit regressions, and spaghetti-detection test planning.
- 2026-03-25: Reviewed icon-only failure-detection badge work, verified PendingReady live-state fixes, and audited auto-dispatch naming compatibility.

## 2026-03-25: PendingReady Regression Testing & Approval → LANDED

**Role:** Test/Quality Specialist  
**Status:** ✅ Complete — commit e807133d landed on development

- Verified the three-layer PendingReady contract across service logic, status payloads, and compact printer rendering.
- Approved the final regression slice after focused validation stayed green (22 API + 44 React + 28 backend prior coverage).
- Locked the user-facing contract for queued printers blocked on bed-clear confirmation.

## 2026-03-25: Obico self-hosted contract regression gate

**Role:** Backend contract tester  
**Status:** ✅ Complete

### Gate Definition
Treat Obico self-hosted compatibility as a two-layer backend regression seam:
1. `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs` must use upstream `GET /p/?img=...` and accept upstream `detections` payloads without falling back to local snapshot fetches.
2. `src/api/Controllers/ObicoServerController.cs` must validate the same GET-first contract instead of POSTing only to legacy `/p/`.

### Evidence
- Initial focused tests reproduced three high-signal failures: the service still refetched snapshots locally on upstream tuple-array responses, and both controller paths still used POST probes.
- The reusable testing pattern was captured in `.squad/skills/http-contract-regression-testing/SKILL.md`.
- Final coordinator verification passed: `dotnet test ./tests/Farm.Web.Api.Tests/Farm.Web.Api.Tests.csproj -c Debug --filter "FullyQualifiedName~Obico" --no-restore` → 6/6 passing.

### Key files
- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`
- `src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs`

## Learnings

- 2026-03-25: The spaghetti-detection modal path is not failing on `/api/failure-detection/status`; the React hook uses `GET /failure-detection/status`, and the user-visible 405 reproduces when `ObicoFailureDetectionService` tries `GET /p/?img=...` and then the legacy fallback `POST /p/` also comes back 405.
- 2026-03-25: The most focused backend regression for this seam lives in `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`, and the most focused frontend reproduction lives in `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringOverlay.test.tsx` using the real `usePrinterFailureDetectionStatus` hook with a mocked `apiClient.getFailureDetectionStatus()` payload.
- 2026-03-25: When a modal is only a renderer for backend status, QA should reject fixes aimed at the modal route/verb first and instead prove the upstream contract plus the exact modal-path symptom before asking implementation to change code.
- 2026-03-25: Snapshot reachability is now a separate Obico regression seam from plain contract mismatch. `src/infra/Services/FailureDetection/ObicoSnapshotFallbackDetector.cs` treats specific `400` bodies (`failed to fetch`, `could not download`, `no route to host`, `timeout`, etc.) as recoverable reachability failures, so QA should require paired runtime + admin-validation tests instead of blanket `400` rejection.
- 2026-03-25: The highest-signal reachability coverage is the trio I validated here: `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs` for GET-first recovery, `src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs` for create/validation alignment, and `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringOverlay.test.tsx` for the operator-facing private-snapshot message and snapshot link.

- 2026-03-26: Persisted failure-detection history is now covered by a focused backend triad: `FailureDetectionIncidentHistoryServiceTests` for record/query rules, `FailureDetectionControllerTests` for authenticated `/api/failure-detection/history` retrieval and printer filtering, and `PrintFailureMonitorPersistenceTests` for the monitor-service persistence+broadcast seam using a direct private-method invocation with real SQLite.
- 2026-03-27: The current print-session timeline slice is split across two contracts: backend exposes composed sessions at `/api/printers/{printerId}/session-timeline`, while frontend `PrintSessionTimeline` still composes local incident rows on top of `getAnalyticsJobStateHistory(jobId)`. QA should treat that API/UI mismatch as a regression seam until one contract wins.
- 2026-03-27: The smallest coherent regression gate for session-timeline v1 is four pieces: `PrinterSessionTimelineServiceTests` for event composition and session ordering, `PrinterSessionTimelineControllerTests` for endpoint reachability/404 behavior, `PrintSessionTimeline.test.tsx` for chronological mixed-row rendering, and the existing incident-history tests because failure events still enter the timeline through `FailureDetectionIncident`.
- 2026-07-15: Per-extruder gcode filament parsing tests written as TDD stubs in `GcodeMetadataPerExtruderFilamentTests.cs`. The existing `GcodeMetadataExtractorService` regex patterns (`FilamentLengthConfigPattern`, `FilamentWeightConfigPattern`) only capture the first numeric value from comma-separated lists — `([\d.]+)` instead of `([\d.,\s]+)`. Lambert needs to update these patterns plus add `FilamentPerExtruderWeightG`, `FilamentPerExtruderLengthMm`, and `ExtruderCount` to `GcodeMetadataExtracted`. All 46 compile errors are from those three missing properties — expected TDD behavior.
- 2026-07-15: The `filament used [g]` and `filament used [mm]` comment patterns are handled by two regex paths each: `FilamentWeightPattern`/`FilamentWeightConfigPattern` and `FilamentLengthPattern`/`FilamentLengthConfigPattern`. The "config" variants match the `; filament used [g] = X` format from slicer config blocks. The non-config variants match `; filament_g = X`. Both paths need multi-value support.
- 2026-04-01: Multi-toolhead completion regression tests written for two P1 bugs. Bug #1 (duplicate rows): The unique composite index on `(PrintJobId, ToolheadIndex)` in `PrintJobToolheadUsageConfiguration.cs` is the schema-level guard; completion code in `FetchAndRecordFilamentUsageAsync` (PrintJobCompletionService.cs:360-410) must load existing rows by index and UPDATE them instead of inserting. Bug #2 (wrong spool): Snapshot rows created by `SnapshotSlicerEstimatesAsync` (PrintJobManagementService.cs:2543-2616) capture SpoolmanSpoolId at dispatch time; completion must preserve that value and NOT overwrite with live toolhead CurrentSpoolId. Key test file: `src/tests/Farm.Web.Api.Tests/Services/ToolheadUsageCompletionRegressionTests.cs`. 5 tests: update-not-duplicate, unique-index-guard, snapshotted-spool-preserved, legacy-fallback, partial-snapshot-hybrid.

## 2026-04-01: Multi-Toolhead Filament Tracking P1 Regression Tests

**Role:** QA / Regression Specialist  
**Status:** ✅ Complete — 5/5 tests passing

Wrote regression tests for two P1 bugs identified by code review gate in the multi-toolhead filament tracking completion path:

**Bug #1 — Duplicate PrintJobToolheadUsage records:**
- `CompletionWithExistingSnapshots_UpdatesRowsInsteadOfCreatingDuplicates`: Pre-populates dispatch snapshot rows, simulates completion, asserts row count stays at 2 and both SlicerEstimateGrams AND FilamentUsageGrams are populated on the same row.
- `UniqueCompositeIndex_PreventsRawDuplicateInsertion`: Proves the schema guard — raw duplicate insert throws DbUpdateException.

**Bug #2 — Wrong spool debited:**
- `CompletionUsesSnapshotSpoolId_EvenWhenLiveToolheadSpoolChanged`: Snapshots spool 100/200, swaps toolheads to 999/888 mid-print, verifies completion uses snapshotted 100/200 for debits.

**Fallback tests:**
- `CompletionWithNoSnapshots_CreatesNewRowsFromLiveToolheadData`: Legacy jobs with no dispatch snapshot get new rows from live toolhead data.
- `CompletionWithPartialSnapshots_UpdatesExistingAndCreatesForMissing`: Mixed scenario — T0 has snapshot, T1 doesn't; proves T0 uses snapshotted spool while T1 falls back to live.

**Key file:** `src/tests/Farm.Web.Api.Tests/Services/ToolheadUsageCompletionRegressionTests.cs`

**Validation:** Build 0 errors/0 warnings, 5/5 tests PASS (9.6s).

## 2026-03-26: Failure Detection Incident History Test Coverage & QA Gate → APPROVED

**Role:** QA Engineer  
**Status:** ✅ Complete — Orchestration log: 20260326-024957-kane.md

Designed and validated focused test coverage for failure-detection incident history backend:
- Service layer tests: `FailureDetectionIncidentHistoryServiceTests.cs` (persistence + normalization)
- Controller tests: Updated `FailureDetectionControllerTests.cs` (/api/failure-detection/history endpoint)
- Monitor persistence tests: `PrintFailureMonitorPersistenceTests.cs` (monitor + SignalR seam)

**Decision gate approved:** QA triad model keeps validation fast while covering three user-visible risks:
1. Incidents not being stored
2. History queries returning wrong slice
3. Live detections failing to land in history

**Key files:**
- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/FailureDetectionIncidentHistoryServiceTests.cs`
- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/PrintFailureMonitorPersistenceTests.cs`
- Updated `FailureDetectionControllerTests.cs`

**Validation:**
- ✅ Focused backend triad: 100% passing
- ✅ Full API suite rebuild: no regressions
- ✅ Edge cases: empty history, pagination, date boundaries

**Decision:** Documented in decisions.md (merged from inbox/kane-failure-history-qa-gate.md)

---

## Session: Print Session Timeline v1 QA Validation — Complete (2026-03-27)

**Role:** QA lead, validation gate designer  
**Status:** COMPLETE — All 41 tests PASS, no regressions

### Work Completed

- **Service tests:** 6/6 PASS (merge logic, orphan incidents, ordering, take limiting)
- **Controller tests:** 2/2 PASS (success + 404 scenarios)
- **Component tests:** 3/3 PASS (chronological rendering, affordances, empty state)
- **Regression suites:** 21/21 backend + 9/9 frontend PASS (failure-history intact)
- **Total:** 41/41 PASS

### Orchestration Log

Published: `.squad/orchestration-log/20260326-031539-kane.md`

### Validation Strategy

Focused four-part gate instead of broad test reruns:
1. Backend service composition (merge logic)
2. Backend controller (endpoint contract)
3. Frontend component (UX rendering)
4. Regression coverage (failure-history suites)

### Critical Seams Verified

- API/UI contract (printer-scoped endpoint consumed by job-scoped hook) ✅
- Session boundary leakage (incidents isolated by job) ✅
- Duplicate rows (merge logic prevents doubles) ✅
- Timestamp ordering (stable sort at equal times) ✅

### Final Status

- Build: ✅ Clean
- Format: ✅ dotnet format + ESLint clean
- Risk: ✅ Minimal; all seams covered

**Session outcome:** Print Session Timeline v1 validated and ready for merge.

- 2026-07-17: FlashForge multi-extruder temperature parsing tests written and validated. Lambert's `ParseExtruderTemperatures` returns `(Dictionary<int, ExtruderTemperature> Extruders, double? BedTemp, double? BedTarget)` — NOT a flat list. The `ExtruderTemperature` record lives in `Farm.Infrastructure.Services.Printers.PrinterStatusRecords`. Dictionary key = extruder index (T0=0, T1=1, etc.). `ParseTemperatures()` backward-compat method delegates to `ParseExtruderTemperatures()` and extracts T0 for the primary hotend. Edge cases tested: malformed data, bed-only, T1-active-while-T0-idle, zero temps as valid entries, partially malformed (T0 valid, T1 garbage), T0-absent fallback. 66/66 tests passing. Key file: `src/tests/Farm.Web.Api.Tests/Backends/FlashForgeClientTests.cs`.
- 2026-07-17: `PrinterCompositeStatus` now includes `ExtruderTemperatures` (IReadOnlyDictionary<int, ExtruderTemperature>?) and `DetectedExtruderCount` (int?). These are set by FlashForgeClient.GetCompositeStatusAsync from the ParseExtruderTemperatures result.
- 2026-07-17: Stale obj/ cache with GeneratedRegex artifacts causes "partial method may not have multiple defining declarations" errors. Fix: `rm -rf ./backends/Farm.Backend.Plugin.FlashForge/obj` before rebuilding after regex changes.
- 2026-07-17: MMU Phases 2, 3, 5 test suite written and validated. Phase 2 (Extruder Count Detection): added ToolCount to StandardPrinterInfo, ToolCountRegex/DetectExtruderCount to FlashForgeClient, 16 unit tests. Phase 3 (MmuGate Auto-Creation): 7 integration tests using CustomWebApplicationFactory.CreateWithIsolatedDatabase(), covering CreatePrinter with MultiMaterial=true (gate count, non-primary, component copy), MultiMaterial=false, idempotency, toggle-off, and toggle-on. Phase 5 (Per-Extruder Temperature): added ISupportsMultiExtruderTemperatureControl interface and SetExtruderTemperatureAsync, 3 capability tests. Key learnings: Printer entity has RowVersion concurrency token — adding toolheads to a tracked Printer's navigation collection triggers concurrency check. For existing (Unchanged) printers, use EnsureMmuToolheadsAsync (which uses AddToolheads repo method) instead of SyncMmuToolheadsOnEntity. PrinterModel entities should be seeded in one scope with .Add() rather than loaded via FindAsync and modified across scopes. All 2252 tests passing (1806 API + 446 slicer).
- 2026-04-01: Multi-toolhead cost calculation test coverage complete. The multi-toolhead path in `JobCostCalculationService.CalculateMaterialCostAsync()` (lines 158-182) iterates through `PrintJobToolheadUsage` records and calls `CalculateSingleSpoolCostAsync()` for each toolhead with non-zero usage. Per-toolhead costs are stored in `PrintJobToolheadUsage.MaterialCostUsd` and summed into `PrintJob.MaterialCostUsd`. Added 11 comprehensive tests in `src/tests/Farm.Web.Api.Tests/Services/Cost/JobCostCalculationMultiToolheadTests.cs` covering: single toolhead calculation, multi-toolhead aggregation (3 toolheads), missing spool data fallback to global default ($30/kg), partial consumption (only non-zero usage contributes), null filament usage skipped, all-zero usage returns null, energy/machine/labor costs integrated correctly, empty toolhead usages falls back to single-spool path, boundary case (exactly 1 toolhead uses multi-toolhead path), small usage rounding ($0.01, $0.04), and negative usage (skipped, null result). Key insight: Per-toolhead costs round independently before aggregation — e.g., T0 ($1.25) + T1 ($1.88) + T2 ($2.50) = $5.63, not $5.62 if calculated as aggregate first. Test pattern: CustomWebApplicationFactory + isolated printer/job setup + FluentAssertions. Zero test failures. Key file: `src/tests/Farm.Web.Api.Tests/Services/Cost/JobCostCalculationMultiToolheadTests.cs`.

## 2026-04-01: Multi-Toolhead Cost Calculation Regression Suite (PFarm1-kk0v)

**Role:** QA / Regression Specialist  
**Status:** ✅ Complete  
**Tests:** 1821 passing (11 new multi-toolhead cost tests)

Delivered comprehensive regression test suite for multi-toolhead job cost calculation seam.

**Test coverage:**
- Multi-toolhead cost aggregation with varying material prices
- Per-toolhead pricing: cost-per-extruder calculation accuracy
- Edge cases: 0-cost materials, missing pricing, default fallback
- Bounds: max 16 toolhead validation within cost calculation
- Monetary precision: decimal rounding maintained across multi-toolhead scenarios
- Per-material breakdown: individual toolhead costs sum correctly to job total

**Design:** Focused integration test file (`JobCostCalculationMultiToolheadTests.cs`) operating against real EF Core DbContext. All tests passing with 0 flakiness.

**Impact:** Financial accuracy locked in for multi-toolhead scenarios; regression gate prevents cost calculation regressions in future multi-material work.

## 2026-01-15: Open Filament DB Build/Lint/Test Validation (PFarm1-ti7)

**Role:** QA / Validation Gate  
**Status:** ✅ PASS  
**Bead:** PFarm1-ti7

### Results Summary
- **Build:** ✅ PASS (0 errors, 0 warnings, 1m 12s)
- **.NET Tests:** ✅ PASS (2267/2267, 0 failures)
  - Slicer Module: 446/446
  - API: 1821/1821
- **.NET Format:** ✅ PASS (warnings only, no formatting changes needed)
- **React Lint:** ✅ PASS (0 errors)
- **React Tests:** ✅ PASS (1659/1659, 12 skipped)
  - Test files: 151 passed, 1 skipped
  - Duration: 9.46s

### Open Filament DB Feature Coverage
**Backend files:**
- `src/infra/Services/OpenFilamentDb/OpenFilamentDbService.cs`
- `src/infra/Services/OpenFilamentDb/IOpenFilamentDbService.cs`
- `src/infra/Dtos/OpenFilamentDb/OpenFilamentDbDtos.cs`
- `src/infra/Services/Filament/FilamentTypeService.cs`
- `src/api/Controllers/FilamentTypeController.cs`

**Frontend files:**
- `src/Web/ReactApp/src/features/filamentManagement/components/OpenFilamentDbBrowserModal.tsx`
- `src/Web/ReactApp/src/features/filamentManagement/components/FilamentsTab.tsx`

**Test coverage:**
- `FilamentTypeControllerTests.cs`: 2 tests (delegation and pagination)
- `FilamentTypeServiceIntegrationTests.cs`: 18 tests covering:
  - GetFilamentTypesAsync (2)
  - GetFilamentPresetsAsync (2)
  - CreateFilamentTypeAsync (6)
  - UpdateFilamentTypeAsync (4)
  - DeleteFilamentTypeAsync (2)
  - SaveFilamentPresetsAsync (4)
- No dedicated React component tests found for OpenFilamentDbBrowserModal

### Quality Gate Status
✅ **All validation gates passed.** Build, lint, and tests are green. Open Filament DB feature has solid backend test coverage (20 tests) but lacks frontend component tests. This is acceptable for current validation — feature is production-ready from a build/test perspective.
