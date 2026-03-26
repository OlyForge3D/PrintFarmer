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

