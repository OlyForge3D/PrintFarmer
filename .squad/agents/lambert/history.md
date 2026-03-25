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

## 2026-03-25: Monitoring route error investigation

**Status:** ✅ Documented

- The `No route to host (...:3333)` message is surfaced by failure-detection monitoring (`PrintFailureMonitorService` / `ObicoFailureDetectionService`), not by a monitoring controller route.
- The backend does not hardcode `10.0.0.24:3333`; the active target comes from `Printer.ObicoServerId -> ObicoServers.Url` first, then global `ObicoSettings.ObicoApiUrl`.
- Operators should inspect `detectionSource` and `detectionTarget` from `GET /api/failure-detection/status` and verify reachability from the API runtime/container before reopening route-bug work.

## 2026-03-25: Obico upstream contract adapter

**Status:** ✅ Implemented and verified

- Self-hosted upstream `ml_api/server.py` on port `3333` uses `GET /p/?img=<snapshot-url>` and returns `{"detections": [...]}`.
- `ObicoFailureDetectionService` now treats that GET contract as primary, parses tuple-style and object-style detection payloads, and only falls back to legacy multipart upload when the server clearly lacks GET support.
- `ObicoServerController` validation follows the same GET-first contract so admin validation and runtime analysis stay aligned.
- Focused verification ended green: coordinator re-ran the Obico test slice and confirmed 6/6 passing.
