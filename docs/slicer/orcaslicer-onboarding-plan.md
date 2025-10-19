# OrcaSlicer Onboarding Plan

Branch: feature/orcaslicer-reimplementation

**Overall Progress: Phases 0, 2, 3 Complete (3/8 total phases incl. Phase 0) – Job Dispatching & Worker Registration Production-Ready; Artifact Storage near completion**

This document contains a phased implementation plan to onboard OrcaSlicer as a self-contained slicer microservice, implement a central registry & job API, and provide optional UI embedding for slicer-published mini-UIs.

## Implementation Status Summary

| Phase | Status | Completion | Notes |
|-------|--------|------------|-------|
| Phase 0: Preparations | ✅ Complete | 100% | Branch created, structure validated |
| Phase 1: Registry & Discovery API | ⏳ Not Started | 0% | Worker discovery & listing (deferred; partial needs already covered by Phase 3) |
| **Phase 2: Job API & Dispatching** | **✅ Complete** | **100%** | **Production-ready with full observability** |
| Phase 3: Worker Registration | ✅ Complete | 100% | Registry + heartbeat + worker sync (SlicersService→Worker) |
| Phase 4: Local Artifact Storage & Job Completion | 🚧 Advanced | 85% | Storage, bulk upload, metrics, thresholds done; final job linkage & retention pending |
| Phase 5: UI Integration | ⏳ Not Started | 0% | Admin UI and embedding |
| Phase 6: Profile Import/Export | ⏳ Not Started | 0% | Orca JSON handling |
| Phase 7: Hardening & Polish | ⏳ Not Started | 0% | Operational excellence |

**Current Focus:** Finalizing Phase 4 (Artifact lifecycle completion + job completion contract) and preparing OrcaSlicer worker adoption of bulk artifact uploads & completion endpoint integration.

Guidelines
- Work in small PRs from branch `feature/orcaslicer-reimplementation`.
- Keep seeding/building of heavy binaries gated by environment flags and `DISABLE_SLICER_BUILDS`.
- Prefers pull-based job model (workers poll queue) for resilience; allow push model later.
- Store raw uploaded profile files for auditability and round-trip export.

Phases

## Phase 0 — Preparations (done / immediate)
- [x] Create feature branch `feature/orcaslicer-reimplementation` (done).
- [x] Confirm `orcaslicer-worker` project and `Dockerfile.orcaslicer` exist (repo already contains these).
- [x] Add this plan in `docs/slicer/orcaslicer-onboarding-plan.md` and track progress.

## Phase 1 — Registry & Discovery API (MVP)
Goal: implement central registry so workers can register themselves and the UI can discover available slicers.

Tasks
- [ ] Add DB entity `SlicerService`:
  - fields: Id, Name, SlicerType, Version, Host, UiManifestUrl, Capabilities (JSON), MaxConcurrentJobs, Status, LastSeen, ApiKey, Tags, CreatedAt, UpdatedAt
- [ ] Add EF migration (if applicable) and persistence code.
- [ ] Implement Controller endpoints:
  - POST `/api/slicers/register` — registers a service and returns a service id and secret/token
  - GET `/api/slicers` — list services (with filtering)
  - GET `/api/slicers/{id}` — details
  - POST `/api/slicers/{id}/heartbeat` — update lastSeen and optional capacity
  - POST `/api/slicers/{id}/deregister` — optional/administrative
- [ ] Add basic policy/auth for service registration (API token or admin-only endpoints)
- [ ] Emit SignalR event `SlicerServiceUpdated` when list changes
- [ ] Add unit/integration tests for registration/heartbeat
- [ ] Add admin UI page under `/settings/slicers` to view/disable services
  
Additional registry & auth details
-- API routes (Phase 1):
  - POST `/api/slicers/register` — body: RegisterSlicerDto. Returns { id, apiKey }.
  - GET `/api/slicers` — list registered services.
  - GET `/api/slicers/{id}` — get single service.
  - POST `/api/slicers/{id}/heartbeat` — heartbeat payload: { status, freeSlots }.
  - POST `/api/slicers/{id}/deregister` — deregister service.

- SignalR hub: `/hubs/slicers` with events `SlicerRegistered`, `SlicerHeartbeat`, `SlicerDeregistered` (see `docs/slicer/hub-contract.md`).

- Optional lightweight auth (Phase 1): set environment variable `SLICER_REGISTRATION_KEY` on the API host to require header `X-Slicer-ApiKey` for register/heartbeat/deregister.

Acceptance criteria
- Services can register and appear in `/api/slicers`
- UI receives SignalR updates when services register/deregister

Estimated effort: 2–3 dev days

## Phase 2 — Job API & Capability-aware Dispatching (COMPLETED ✅)
Goal: Slice job API, capability-based worker dispatch, lifecycle events, and UI integration.

**Status: FULLY IMPLEMENTED with comprehensive hardening completed Oct 19 2025**

Core Implementation (100% Complete)
- [x] SliceJob entity with capability JSON, priority, lifecycle timestamps (`SliceJob.cs`)
- [x] Enqueue endpoint `POST /api/slice` with profile resolution & capability validation
- [x] Status endpoint `GET /api/slice/{id}`
- [x] Cancellation endpoint `POST /api/slice/{id}/cancel`
- [x] User jobs listing `GET /api/slice/my-jobs`
- [x] Queue listing `GET /api/slice/queue` (admin-restricted via policy)
- [x] Worker capability registration (Orca & Prusa workers)
- [x] Capability-aware filtering (`EfWorkerRepository.GetWorkersByCapabilitiesAsync`)
- [x] Scoring & dispatch logic (load, speed, success rate, capability bonus)
- [x] SignalR lifecycle events (queued, started, progress, completed, failed, cancelled)
- [x] Frontend pages/services (`NewSliceJobPage`, `JobQueueDashboardPage`, `sliceJobService.ts`)

Hardening Completed (Oct 19 2025)
- [x] **Retry logic with exponential backoff** (3 attempts, 250ms base, 2x multiplier)
- [x] **Configurable retry parameters** (`RetryOptions` class, `JobDispatchRetry` config section)
- [x] **Rate limiting externalized** (`RateLimiting:SliceJobs` with 20/hour, 200/day limits)
- [x] **Policy-based authorization** (`CanViewSliceQueue` requiring `farm_admin` role)
- [x] **Comprehensive metrics instrumentation**:
  - Counter: `slicing_jobs_dispatched`
  - Counter: `slicing_jobs_dispatch_failed` (with reason tags)
  - Histogram: `slicing_job_dispatch_duration_ms` (with outcome tags)
  - Gauge: `slicing_available_workers`
- [x] **Prometheus/OpenTelemetry export** (via `/metrics` endpoint)
- [x] **Stale worker filtering** (`SLICER_WORKER_STALE_SECONDS` env, default 120s)
- [x] **Capability validation** (max 32, distinct values, slug format regex)
- [x] **Worker pull/claim model** (`POST /api/slice/claim` with lease semantics)
- [x] **Lease management** (ClaimedAt, LeaseExpiresAt fields for job timeout)
- [x] **Unit test coverage** (retry tests, rate limit tests, capability validation tests)

Observability & Operations
- [x] Metrics exported via Prometheus scraping endpoint
- [x] OTLP exporter configured for external telemetry backends
- [x] Dispatch duration tracking with success/failure/error outcomes
- [x] Real-time worker availability monitoring

Deferred (Optional Future Enhancements)
- [ ] Circuit breaker for repeated worker failures (not critical for MVP)
- [ ] Audit logging infrastructure (compliance feature, independent of core flow)
- [ ] Negative tests for malformed capability JSON edge cases
- [ ] Advanced worker selection algorithms (ML-based scoring)

Acceptance Criteria (All Met ✅)
- ✅ API enqueues slice jobs with capability constraints
- ✅ Workers selected based on capabilities & scoring
- ✅ Lifecycle events broadcast over SignalR
- ✅ Retry logic handles transient failures
- ✅ Metrics exported for monitoring/alerting
- ✅ Pull model supports worker-initiated job claiming
- ✅ Configuration externalized for operational flexibility

Test Results
- Build: ✅ Success (0 errors)
- Unit Tests: ✅ 5/5 passing (JobDispatcher, Retry, RateLimit)
- Integration: ✅ Validated with live API server

Update History
- 2025-10-19: Phase marked COMPLETE after implementing full feature set
- 2025-10-19: Added capability validation, rate limiting, policy auth, metrics
- 2025-10-19: Completed hardening: Prometheus export, configurable retry, worker pull model, stale filtering
- 2025-10-19: All tests passing, production-ready

## Phase 3 — Worker Registration (✅ COMPLETE)
Goal: Worker self-registration, heartbeat capacity updates, and unified dispatcher visibility.

Delivered
- [x] `SlicersService` implements register/heartbeat/deregister endpoints
- [x] Worker synchronization: creation/update/offline mapping to `Worker` entity
- [x] Heartbeat propagates `FreeSlots`, derives `ActiveJobs`, maps status → `WorkerStatus`
- [x] SignalR hub broadcasts: `SlicerRegistered`, `SlicerHeartbeat`, `SlicerDeregistered`
- [x] Unit tests: registration, heartbeat, deregistration sync (see `SlicersServiceWorkerSyncTests`)
- [x] Integration scaffold deferred (Prometheus MeterProvider requirement) — to re-enable post telemetry test host configuration

Acceptance Criteria (Met)
- Workers appear immediately in dispatcher queries (`EfWorkerRepository`)
- Heartbeats adjust load metrics without stale-tracking regressions
- Deregistration marks worker Offline preserving historical metrics

Follow-ups
- [ ] Add draining workflow (graceful capacity reduction before offline)
- [ ] Re-enable full integration test once test host wires `MeterProvider`

## Phase 4 — Local Artifact Storage & Job Completion (🚧 In Progress)
Goal: Persist slicing outputs (G-code, previews, logs) on the user's own hardware (no cloud dependency) and finalize job lifecycle with robust integrity metadata.

Design Principles
1. 100% local-first: artifacts stored under a configurable root (default `artifacts/` adjacent to `wwwroot`).
2. Content-address hinting: include SHA-256 hash for integrity & optional dedup later.
3. Streaming friendly: avoid buffering large uploads fully in memory.
4. Separation of concerns: metadata in DB, bytes on disk; never store large blobs in RDBMS.
5. Stable public URL pattern for UI consumption: `/artifacts/{yyyy}/{MM}/{dd}/{artifactId}/{originalName}` (served as static files or minimal passthrough).

New Domain Model (Artifact)
| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | Primary key |
| JobId | Guid | Links to slice job (future: foreign key) |
| WorkerId | Guid? | Producing worker (optional for legacy jobs) |
| FileName | string | Original client / slicer provided name |
| RelativePath | string | Path under Root (for relocation) |
| ContentType | string | `text/plain`, `application/gcode`, `image/png` |
| SizeBytes | long | For UI progress & cleanup policies |
| Sha256 | string | Hex; computed server-side |
| Kind | string | `gcode`, `thumbnail`, `preview`, `log` |
| CreatedAt | DateTime | Stored UTC |

Configuration
```jsonc
"ArtifactStorage": {
  "RootPath": "artifacts",        // relative or absolute
  "MaxFileSizeBytes": 104857600,   // 100MB default guard
  "AllowedKinds": "gcode,thumbnail,preview,log"
}
```

Service Contract (ArtifactsService)
- UploadAsync(IFormFile file, Guid jobId, Guid? workerId, string kind) → ArtifactDto
- GetAsync(Guid id)
- ListByJobAsync(Guid jobId)
- StreamAsync(Guid id) (optional passthrough for auth policies)

API Endpoints
- `POST /api/artifacts` (multipart/form-data)
  - Fields: jobId, kind (enum), workerId (optional), file
  - Validates size, kind, content type, extension (e.g. `.gcode`, `.png`)
  - Returns ArtifactResponse with metadata + URL
- `GET /api/artifacts/{id}` — metadata
- `GET /api/artifacts/job/{jobId}` — list artifacts for job
- `GET /api/artifacts/{id}/download` — file stream (if not served directly by static hosting)

Security & Integrity
- Reject path traversal (`file.FileName` sanitized; never trust client path segments).
- Compute SHA-256 to allow future dedup & integrity checks.
- Enforce max size; return 413 if exceeded.
- Optional API key / user auth based on job ownership for download.

Local Filesystem Layout
```
<RootPath>/
  2025/
    10/
      19/
        <artifact-guid>/
          original-name.gcode
          preview-1.png
```

Job Completion Flow Update
1. Worker finishes slicing → produces G-code + thumbnails.
2. Worker POSTs artifacts (or sends presigned-like metadata; MVP: direct upload to API).
3. Worker POSTs `/api/slice/{id}/complete` with artifact IDs + summary metrics.
4. API transitions job status to Completed; broadcasts SignalR event with artifact URLs.

Testing Strategy (Phase 4)
- Unit: path sanitizer, hash calculator, size validator.
- Integration: upload + metadata persistence + disk existence; completion endpoint associates artifact.
- Edge: oversize file, unsupported kind, duplicate filename within same artifact folder.

Metrics (to add)
- Counter: `artifacts_uploaded_total` (tags: kind)
- Histogram: `artifact_upload_bytes` (distribution)
- Gauge: `artifact_storage_total_bytes` (periodic scan; optional post-phase)

Future Enhancements
- Periodic re-hash verification task.
- Compression for large textual logs.
- Automatic thumbnail derivation registry.
- Cleanup policy (LRU or age-based) with dry-run mode.

Current Progress (85%)
Implemented & Verified:
- Artifact entity, persistence, and filesystem layout
- Single and bulk upload endpoints (`POST /api/artifacts`, `POST /api/artifacts/bulk`)
- Download & metadata endpoints (`GET /api/artifacts/{id}`, `GET /api/artifacts/{id}/download`, `GET /api/artifacts/job/{jobId}`)
- Stable URL contract surfaced via controller mapping (with optional `PublicUrl` when static serving enabled)
- SHA-256 integrity hashing stored with metadata
- Kind validation with structured 400 response (allowed kinds configurable)
- Bulk upload: atomic multi-file processing with kind inference (MIME + extension fallback)
- Metrics instrumentation:
  - Counter: `artifacts_uploaded_total` (tag: kind)
  - Histogram: `artifact_upload_bytes`
  - Gauge: `artifact_storage_total_bytes`
  - Threshold state gauge + events (warning/critical) for storage utilization
- Threshold alerting events (single transition semantics) with logging subscription
- Optional static file hosting for artifacts (disabled by default, configurable `EnableStaticServing`)
- App configuration block `ArtifactStorage` extended with thresholds & static serving toggles
- Comprehensive tests (13 passing) covering validation, metrics, thresholds, bulk scenarios

Remaining Work:
- Link artifacts to slice jobs (associate JobId/WorkerId in completion flow)
- Implement `/api/slice/{id}/complete` integration returning artifact IDs & final status
- Add retention / cleanup policy draft (age + size threshold dry-run)
- Optional dedup (future; leverage SHA-256) & rehash verification background task
- Formalize security model for download authorization (job ownership / admin roles)

Risk & Considerations:
- Potential rapid storage growth without retention (mitigate via upcoming cleanup policy)
- Worker compatibility requires updated completion contract including artifact references
- Static serving toggle must remain off by default to avoid unintended public exposure

Updated Acceptance Criteria (Implemented items marked ✅; pending items ⏳):
- ✅ Upload endpoints store file + metadata and return stable (or public when enabled) URL
- ✅ Bulk upload supports multi-artifact submission with kind inference
- ✅ Integrity hash recorded for each artifact
- ✅ Metrics & thresholds exposed for observability/alerting
- ⏳ Completion endpoint finalizes job status and links artifacts
- ⏳ Authorization rules for artifact access refined (ownership / RBAC)
- ⏳ Retention policy documented & initial implementation (dry-run mode)

Acceptance Criteria (Planned)
- Upload endpoint stores file + metadata and returns stable URL.
- Completion endpoint updates job status and links artifacts.
- Files accessible locally without cloud dependencies.

Estimated Effort Remaining: 2–3 dev days (foundational), +1 day hardening.

## Phase 4 — Worker Job Processing & Artifact Uploads
Goal: Ensure the worker processes jobs, writes artifacts to object storage, and posts results.

Tasks (Updated)
- [ ] Finalize job completion contract: jobId, status, logs, artifactIds, metrics summary
- [ ] Worker posts artifacts via bulk endpoint prior to completion (capture returned IDs)
- [ ] Implement `/api/slice/{id}/complete` linking existing artifacts (no re-upload inline)
- [ ] Add GCode domain resource or extend SliceJob with Artifact references
- [ ] Progress updates & incremental log streaming (optional; defer if blocking schedule)
- [ ] End-to-end tests: enqueue → claim → worker bulk upload → completion → artifact retrieval
- [ ] Authorization checks: only job owner or admin can list/download associated artifacts (unless static serving public)

Acceptance criteria (Revised)
- Completed jobs reference all artifact IDs and expose them via status endpoint
- Artifact metadata accessible & downloadable (honoring auth/public configuration)
- No duplicate artifact uploads in completion payload
- Metrics reflect storage growth and upload distribution

Estimated effort: 2–4 dev days

## Phase 5 — UI integration & optional slicer UI embedding
Goal: Expose registered slicers in the UI and optionally embed slicer-published mini-UIs.

Tasks
- [ ] Expose `/settings/slicers` admin UI showing registered services and their capabilities
- [ ] In the Slicer configuration flow show available server-side slicers and recommended services
- [ ] Show real-time status (SignalR) for active jobs and available capacity
- [ ] Implement UI embedding model (sandboxed iframe preferred):
  - Worker provides `uiManifestUrl` when registering
  - Main UI offers an `Embed advanced slicer UI` button that opens a sandbox iframe to the `uiManifestUrl`
  - Use postMessage bridge for job requests/actions from embedded UI to the parent (main API) if needed
- [ ] Provide a fallback link to open worker UI in a new tab

Acceptance criteria
- UI lists registered slicers and can open embedded UI in sandboxed iframe

Estimated effort: 3–5 dev days

## Phase 6 — Profile import/export, seeding & admin UX
Goal: Seed built-in Orca profiles, allow user import of custom Orca JSON files and map profiles to registered printers.

Tasks
- [ ] Implement Orca JSON parser in API (parse, normalize, return preview)
- [ ] POST `/api/profiles/import/orca` — preview parsed profiles and suggested mappings to registered printers
- [ ] UI import wizard to map parsed printer/filament/process presets to registered printers or create new ones
- [ ] Seeder job to seed official Orca built-in profiles into DB as read-only
- [ ] Exporter to generate Orca JSON or 3MF from canonical profiles
- [ ] Tests & sample profile suite

Acceptance criteria
- Built-in profiles seeded and import UI works for uploaded Orca JSON

Estimated effort: 3–6 dev days

## Phase 7 — Hardening & operational polish
- [ ] Add auth (per-service tokens, rotation), RBAC for admin UIs
- [ ] Add observability (metrics for job durations, failure rates, per-service capacities)
- [ ] Add resource limits and sandboxing best-practices to worker Dockerfiles
- [ ] Add CI checks for worker builds but keep strict builds manual/guarded
- [ ] Document runbook for seeding / updating built-in profiles

Estimated effort: 3–6 dev days


---

## Quick start for local dev (how to test incrementally)
- Checkout `feature/orcaslicer-reimplementation` (already done)
- Start the API and DB locally per `LOCAL_DEVELOPMENT.md`
- Implement and run Phase 1 endpoints locally (dotnet run the API)
- Start `orcaslicer-worker` in dev mode (it has stubs if binary missing) and verify registration
- Enqueue a sample slice job via API and watch the worker claim it


## File additions (initial)
- `docs/slicer/orcaslicer-onboarding-plan.md` (this file)
- `src/api/Controllers/SlicersController.cs` (new)
- `src/api/Models/SlicerService.cs` (new)
- `src/orcaslicer-worker/Services/RegistryClient.cs` (new)
- `src/Web/ReactApp/src/pages/SlicersPage.tsx` (new admin UI)


## Notes
- Keep heavy binary builds gated and optional. Use `ALLOW_STUB` and `DISABLE_SLICER_BUILDS` during development.
- Prefer the pull-based job model initially to reduce cross-network push complexity.

---

## What's Next: Recommended Path Forward

With Phase 2 complete and production-ready, there are two viable paths:

### Option A: Continue Sequential Implementation (Recommended)
**Next Phase: Phase 3 - Worker Registration**
- Implement worker self-registration with the API
- Add heartbeat mechanism for capacity reporting
- Enable graceful deregistration on shutdown
- Benefits: Completes the worker lifecycle management before processing jobs

### Option B: Jump to Job Processing (Faster MVP)
**Next Phase: Phase 4 - Job Processing & Artifacts**
- Implement end-to-end job execution in workers
- Add artifact uploads (G-code, previews) to object storage
- Complete job lifecycle with results posting
- Benefits: Achieves end-to-end slicing capability faster

### Completed Features Ready for Use
The following are production-ready and can be used immediately:
- ✅ Job submission API with capability filtering
- ✅ Worker selection with intelligent scoring
- ✅ Pull-based job claiming (workers can poll for jobs)
- ✅ Comprehensive metrics and monitoring
- ✅ Rate limiting and authorization
- ✅ SignalR real-time updates

### Dependencies for Full System
To achieve a fully functional slicing pipeline:
1. **Worker must register** (Phase 3) OR use manual worker configuration
2. **Worker must process jobs** (Phase 4) to generate actual G-code
3. **Optional**: Phase 5-7 for UI polish, profile management, and operational features

---

Update log
- 2025-10-19: Phase 2 marked COMPLETE with comprehensive hardening (retry, metrics, pull model, tests)
- 2025-10-19: Added overall progress summary and recommended next steps
- 2025-10-12: Created and committed initial plan. Branch: `feature/orcaslicer-reimplementation`.



