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

## 2026-03-27: Failure Detection Timeline Decision — NO PERSISTENCE LAYER NEEDED

**Role:** Backend affected  
**Status:** Recommendation from Dallas (Lead) — Ready for implementation

From Dallas decision: Failure detection is a real-time monitoring state machine (in-memory `FailureDetectionPrinterStatusDto`), not a persisted historical audit log. Recommendation is to **NOT implement a timeline view**. Current in-memory snapshot suffices.

**Next steps for Lambert:**
- Current in-memory snapshot is sufficient; no persistence layer needed.
- If future requirement for audit logging surfaces (security/compliance), that's a separate decision and data-model change.
- See decision entry in `.squad/decisions.md` (entry 4) for full context and open questions.

**Files:**
- `src/infra/Services/FailureDetection/FailureDetectionMonitorStatus.cs` — In-memory DTO, sufficient as-is
- No schema changes, no scan-history table needed

## Learnings

- 2026-03-26: `/api/failure-detection/status` already exposes the operator-facing monitoring reason/source/target/outcome contract. For richer PrintFarmer-owned UX, the safest backend addition is optional `jobName`/`fileName` on `FailureDetectionPrinterStatusDto` and SignalR `FailureDetectionDto`, sourced from `IPrinterStatusCacheReader` with queued-job fallback in `PrintFailureMonitorService`.
- 2026-03-25: Upstream `moonraker-obico` is a co-located agent, not just an ML client. It links to Obico with a server auth token, talks directly to Moonraker with API-key/WebSocket access, captures JPEGs locally, and can tunnel HTTP/WebSocket traffic plus Janus-based webcam streaming.
- 2026-03-27: PrintFailureMonitorService updates in-memory status every 30s scan cycle. No persistence layer = no historical queries, no timeline = no schema change burden.
- 2026-03-25: PrintFarmer’s failure-detection path is central-server driven. `PrintFailureMonitorService` selects the first enabled camera with a `SnapshotUrl` (or legacy `Printer.CameraSnapshotUrl`) and passes that URL to `ObicoFailureDetectionService`; stream-only cameras are currently ignored.
- 2026-03-25: The concrete backend gaps exposed by the plugin comparison are snapshot delivery and reachability, not full plugin parity: PrintFarmer needs a first-class proxy/upload path for private or authenticated webcams, printer-aware reachability validation, and aligned GET-fallback behavior between runtime and admin validation.
- 2026-03-25: Moonraker auth handling is materially weaker than the upstream plugin. `moonraker-obico` fetches and uses Moonraker API keys for REST/WebSocket access, while PrintFarmer’s Moonraker client currently ignores `PrinterCredential` on camera-discovery and related paths.
- 2026-03-25: Key paths for future Obico/Moonraker gap work: `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`, `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`, `src/api/Controllers/ObicoServerController.cs`, `src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs`, and `src/api/Controllers/PrintersController.cs`.

- The spaghetti detection modal does not call Obico directly; it renders the cached per-printer snapshot returned by `GET /api/failure-detection/status` from `FailureDetectionController`, which is populated by `PrintFailureMonitorService`.
- `PrintFailureMonitorService` stores `FailureDetectionResult.ErrorMessage` verbatim in `FailureDetectionPrinterStatusDto.Reason`, so raw upstream contract errors surface in the modal unless `ObicoFailureDetectionService` converts them into actionable messages.
- For Obico compatibility, `GET /p/?img=...` stays the preferred contract, but a legacy `POST /p/` probe returning `405` is not a healthy fallback. `ObicoServerController` add/enable validation must reject that case so runtime monitoring and settings validation stay aligned.
- Key files for this regression seam: `src/api/Controllers/FailureDetectionController.cs`, `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`, `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`, and `src/api/Controllers/ObicoServerController.cs`.
- 2026-03-25: Obico snapshot reachability now has a narrow fallback rule: if `GET /p/?img=...` returns `400` with snapshot-fetch wording (fetch/download/no route/connection refused/timeout), runtime and `ObicoServerController` both retry the legacy local-upload path instead of surfacing a raw compatibility error. Key files: `src/infra/Services/FailureDetection/ObicoSnapshotFallbackDetector.cs`, `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`, `src/api/Controllers/ObicoServerController.cs`, and the focused Obico controller/service tests.

## 2026-03-26: Obico Plugin Gap Analysis — Guidance Finalized

**Role:** Backend architect / guidance contributor  
**Status:** ✅ Complete — Analysis documented in decisions.md

**Context:** PrintFarmer's Obico snapshot delivery implementation reviewed against upstream Moonraker-Obico plugin. Goal: identify gaps, prioritize follow-up work, align with farm-controller architecture (not single-printer agent).

**Architecture Difference Clarified:**
- **Moonraker-Obico:** Co-located Moonraker agent; can rely on localhost webcam, Moonraker API-key auth, Janus/WebRTC relay
- **PrintFarmer:** Farm controller; must handle remote printer discovery, HTTP-based snapshot delivery, selective auth

**Gap Analysis (Priority-ordered):**

**Required (Current ML-monitoring integration):**
1. ✅ Snapshot delivery to Obico ML API (direct camera URL when reachable; proxy/upload fallback)
2. ⚠️ Short-lived/tokenized snapshot endpoint (if external Obico servers need `GET /p/?img=...`)
3. ✅ Align runtime fallback with validation (ObicoFailureDetectionService + ObicoServerController both treat 400 as legacy signal)
4. ✅ Printer-aware reachability validation (prove Obico server can reach real camera path)
5. ⚠️ Strengthen Moonraker auth support (use PrinterCredential in camera URL discovery)

**Lower Priority (Follow-up):**
- Support stream-only webcams by deriving snapshots from `stream_url`

**Out of Scope (Different product area):**
- Remote relay, Obico account linking, passthru APIs, Janus streaming (Obico's responsibility; do NOT add)

**Key Takeaway:** Current snapshot delivery implementation is **correct and sufficient** for local failure detection. Do NOT attempt to replicate upstream Moonraker-Obico plugin's full feature set; maintain separation of concerns between farm controller (PrintFarmer) and cloud service provider (Obico).

**Files:** Documented in decisions.md; informs future backend planning.

## 2026-03-26: Obico Plugin Gap Analysis — Backend Architecture Validation

**Role:** Backend architect + team lead  
**Status:** ✅ Complete — Analysis documented and validated

**Team Collaboration:**
- Confirmed with Brett that OctoPrint plugin sends full printer/job/session state
- Established with Parker that current Obico snapshot implementation is correct
- Validated that PrintFarmer intentionally uses only ML/failure-detection slice

**Key Backend Findings:**
1. PrintFarmer's ML snapshot delivery is **correct and sufficient** for local failure detection
2. Farm-controller architecture (multi-tenant) differs from single-printer-agent model (Moonraker-Obico)
3. Current GET-first/fallback snapshot contract is **properly aligned** between runtime and validation
4. Do NOT replicate Moonraker-Obico's full feature set (WebRTC, tunneling, account linking)

**Architecture Principle:**
- PrintFarmer: Multi-printer farm controller (own truth source)
- Obico: Cloud ML/monitoring service (external consumer only)
- Maintain separation of concerns; PrintFarmer is authoritative for printer/job state

**Follow-up Guidance (Lower Priority):**
- Support stream-only webcams by deriving snapshots (future enhancement)
- Strengthen Moonraker auth support via PrinterCredential (future optimization)
- Do NOT add remote relay, account linking, or Janus streaming (out-of-scope)

**Files:** Documented in decisions.md; orchestration logs (`2026-03-26T01-45-41Z-lambert.md`).


## 2026-03-27: Failure Detection Backend — Timeline Scope Clarification (Cross-Agent)

**Input:** Dallas decision: failure detection does not need historical scan persistence  
**Status:** Pending team decision

Backend failure-detection scope clarified: Current in-memory snapshot (PrintFailureMonitorService) is sufficient. No timeline/event-store needed for MVP. If future compliance/audit requirement surfaces, that's a separate data-model decision. Awaiting team approval.

## 2026-03-26: Failure Detection Backend — Job Context Enrichment → LANDED

**Role:** Backend Dev  
**Status:** ✅ Complete — Orchestration log: 20260325-193351-lambert.md

- Extended `FailureDetectionPrinterStatusDto` and SignalR `FailureDetectionDto` with optional `jobName` and `fileName` fields
- Implemented context resolution in `PrintFailureMonitorService`: cache-first + fallback to active queue record
- Updated `ObicoFailureDetectionService` to surface resolved job context
- SignalR hub broadcasts enriched events with complete alert identification

**Validation:**
- 25 focused failure-detection backend tests passed
- Context resolution logic validated (cache-hit and fallback paths)
- Backward compatibility confirmed with null field handling
- API build passed with 0 new errors

**Key integration:**
- Frontend alerts now arrive with job identification via enriched SignalR events
- PrintFarmer remains UX source of truth; no state duplication into Obico
- Seamless enrichment without breaking existing deployments
- Removed barrier to operator understanding which print is being monitored

**Known gap:** Historical job context for past-session incidents requires backend history endpoint (descoped from current work)

## 2026-03-26: Failure Detection Incident History Backend Foundation → LANDED

**Role:** Backend Dev  
**Status:** ✅ Complete — Orchestration log: 20260326-024957-lambert.md

Implemented end-to-end backend persistence layer for failure-detection incident history:
- Domain aggregate: `src/infra/Domain/FailureDetectionIncident.cs`
- EF Core config: `src/infra/Data/Configurations/FailureDetectionIncidentConfiguration.cs`
- Query service: `src/infra/Services/FailureDetection/FailureDetectionIncidentHistoryService.cs`
- Monitor persistence: Enhanced `PrintFailureMonitorService` to record incidents on detection
- API endpoint: `FailureDetectionController.GET /api/failure-detection/history`
- Migrations: PostgreSQL + SQL Server, both idempotent and passing

**Key files:**
- `src/infra/Domain/FailureDetectionIncident.cs`
- `src/infra/Services/FailureDetection/FailureDetectionIncidentHistoryService.cs`
- `src/api/Controllers/FailureDetectionController.cs`
- `src/migrations/Farm.Migrations.PostgreSQL/` (FailureDetectionIncident table)
- `src/migrations/Farm.Migrations.SqlServer/` (equivalent SQL Server)

**Validation:**
- ✅ Clean rebuild (dotnet build)
- ✅ Full API test suite passing
- ✅ Focused backend triad passing (Kane's QA gate validation)

**Next:** Ripley integrates frontend UX. Backend ready for production queries.

---

