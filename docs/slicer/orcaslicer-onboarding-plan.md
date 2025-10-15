# OrcaSlicer Onboarding Plan

Branch: feature/orcaslicer-reimplementation

This document contains a phased implementation plan to onboard OrcaSlicer as a self-contained slicer microservice, implement a central registry & job API, and provide optional UI embedding for slicer-published mini-UIs.

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

## Phase 2 — Job API & Capability-aware Dispatching
Goal: Add slice/preview job API and routing rules so jobs carry required capabilities.

Tasks
- [ ] Add SliceJob DB entity and enqueue API endpoint(s):
  - POST `/api/slice` — create a job (preview|full) with modelId, compositeProfileId, requiredCapabilities
  - GET `/api/slice/{id}` — job status
- [ ] Extend job payload to include canonical capability fields (slicerType, requiredNozzle, extruders, previewInfo)
- [ ] Dispatcher logic (server-side): when enqueueing, optionally determine candidate slicer services from registry (for optional push).
- [ ] Worker-side contract for pull model: workers claim/lease messages from queue, verifying they can satisfy requiredCapabilities (capabilities match)
- [ ] Add job lifecycle events (queued, claimed, running, succeeded, failed, canceled) and SignalR notifications
- [ ] Tests: unit tests for enqueueing & capability matching, end-to-end simulated worker claim flow

Acceptance criteria
- API can enqueue slice jobs with capability constraints
- Worker consumer can claim jobs and report status updates

Estimated effort: 2–4 dev days

## Phase 3 — Worker Registration (Integrate with existing worker)
Goal: Wire orcaslicer-worker to register and heartbeat with the new registry.

Tasks
- [ ] Implement registration client in `src/orcaslicer-worker/Program.cs`:
  - On startup, POST `/api/slicers/register` with capabilities, receive service id + token
  - Store token locally (ephemeral) and use for subsequent API calls
- [ ] Implement periodic heartbeat POST `/api/slicers/{id}/heartbeat` with free-slot/capacity data
- [ ] Implement graceful deregister on SIGTERM
- [ ] Optionally: support config `SLICER_REGISTRY_URL` and `SLICER_SERVICE_NAME`
- [ ] Add health & readiness interplay: if worker lacks orca binary, mark orca_binary check as unhealthy but still register (if `ALLOW_STUB` true)

Acceptance criteria
- Orca worker registers successfully and appears in UI/registry
- Worker sends capacity updates and deregisters on shutdown

Estimated effort: 1–2 dev days

## Phase 4 — Worker Job Processing & Artifact Uploads
Goal: Ensure the worker processes jobs, writes artifacts to object storage, and posts results.

Tasks
- [ ] Standardize job result contract: jobId, status, logs, artifactUrls (gcodeUrl, previewUrls), metadata
- [ ] Worker uploads artifacts to object storage (local disk in dev or S3 in prod) and posts job completion to `/api/slice/{id}/complete`
- [ ] API validates result and creates GCode resource linking artifacts and metadata
- [ ] Worker should support progress updates and small incremental log streaming (optional)
- [ ] Tests: end-to-end with dev object storage and worker stub generating sample gcode/preview files

Acceptance criteria
- Completed jobs create GCode resources and artifacts are accessible

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

Update log
- 2025-10-12: Created and committed initial plan. Branch: `feature/orcaslicer-reimplementation`.



