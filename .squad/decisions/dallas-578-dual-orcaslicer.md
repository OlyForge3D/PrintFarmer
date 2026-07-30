# Decision: Dual-engine OrcaSlicer support (issue #578)

- **Date:** 2026-07-13
- **Author:** Squad / Dallas (lead) on branch `jpapiez-squad-578-dual-orcaslicer` (base `feature/705-operator-redesign` @ `2564ea477`)
- **Scope:** GitHub issue #578 only. **Does not touch** #741 or PrusaSlicer wiring.
- **Rubber-duck:** claude-opus-4.8 @ max, delivered blocking findings; design revised accordingly.

## Approved topology

**Two engine-versioned worker containers, capability-tagged version routing, submit-time version resolution.**

Rejected: one image with two binaries + per-job runtime binary selection. Reason: doubles worker-side cache/state complexity and breaks the "one worker = one advertised version" contract that today's registration/heartbeat/lease bookkeeping assumes.

## Concrete design (post-rubber-duck)

### 1. Plugins
- Add `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/` peer to v2_4_0 (mirror structure; `SlicerVersion = "2.3.1"` — MUST parse as `System.Version`; no `x` placeholders).
- `SlicerRegistry` is already multi-version-safe — **no registry code changes**.
- **Full shipping checklist for every plugin add/drop** (fails silently if any is missed): `src/api/Farm.Web.Api.csproj` `<ProjectReference>` + `_SlicerPluginDll` include; `src/farm-web.sln`; `scripts/docker/dockerfiles/Dockerfile.multistage` restore + publish + `_SlicerPluginDll` copy into slicer-host publish dir; `Slicer:PluginsPath` env for `Farm.Slicer.Host`; test project references.
- Startup assertion at API/host boot: `ISlicerRegistry.ListAvailableSlicers().Any(...)` count matches expected value from config; fail readiness on mismatch.
- Fix `SlicerPluginDiscovery` static list de-dup across calls (belt-and-suspenders — prevents cross-host contamination in test hosts).

### 2. Persistence
- Add nullable `SliceJob.SlicerEngineVersion string?(32)` column. Semantics: NULL = "legacy / any version" (back-compat); non-NULL = strict version routing.
- Migrations: **both** `Farm.Slicer.Migrations.PostgreSQL` and `Farm.Slicer.Migrations.SqlServer` (SlicerDbContext). No AppDbContext / `Farm.Migrations.*` touches.
- SQLite dev uses `EnsureCreated`; new column appears automatically.
- **Do NOT** add a `SlicerLibrary` table, FK, composite type, or JSON column. Plain sibling string next to `SlicerEngine int` — libraries are in-code plugins with no DB backing.

### 3. Capability-tagged routing (the correctness-critical section)
- Worker `CapabilitiesJson` at both **registration** (`SlicerRegistrationClient`) and **claim** (`QueueConsumerService.GetWorkerCapabilities`) becomes: `["orcaslicer", "orcaslicer:<version>", "stl-processing", "gcode-generation"]`. Version sourced from a single config key (new `Worker:EngineVersion`, falling back to `SlicerRegistry:Version`, then binary detection) so registration and claim always agree.
- **Job's `RequiredCapabilitiesJson`** — critical, because `EfSliceJobRepository.ClaimNextJobAsync` is an OR-match (any single tag → match):
  - `SlicerEngineVersion` present → `["orcaslicer:2.4.0"]` **only** (no bare `orcaslicer` tag; otherwise a wrong-version worker matches on the generic tag).
  - `SlicerEngineVersion` NULL → `["orcaslicer"]` (matches any worker — legacy behavior).
- **Server-side derivation** in `SliceJobController.SubmitAsync` (the live path — `SlicerOrchestrator.SubmitJobAsync` is legacy/dead). Overrides any client-supplied `RequiredCapabilitiesJson` for the engine tag. Version resolved to latest **at submit time** (never at claim time — claim-time resolution would let a later engine upgrade retroactively change how an already-queued job slices).
- Second worker MUST have distinct `SlicerRegistry:Host`, `Worker:WorkerId`, `Worker:InstanceId`, and `SlicerRegistry:ServiceName`. The `SlicersService` upsert keys on `EndpointUrl`, so identical hosts would clobber each other's Worker rows.

### 4. API contracts (all additive, camelCase, string enums)
- `SubmitSliceJobRequest` / `SlicingJobRequest`: new optional `slicerEngineVersion string?`.
- `SlicerEngineInfo`: keep existing `version` scalar; add `availableVersions string[]`. Backfilled from `ISlicerRegistry.GetLibraries(name).Select(l => l.SlicerVersion)` — requires injecting `ISlicerRegistry` into whichever `/engines` endpoint the frontend actually consumes. Also fix the stale hard-coded `"1.8.x"` in `SlicerOrchestrator._engineCatalog` while touching it.
- Slicer profile / metadata / asset API endpoints accept optional `?version=` query param; default = latest via `GetLatestLibrary(name)`; scopes to `(engine, version)` via `SlicerRegistry.GetLibrary`.
- SignalR `SliceJobEventDto`: add nullable `slicerEngineVersion` field. No event-name changes (event names stay lowercase per contract).

### 5. Worker fail-fast (drift protection)
- New startup step in `Farm.OrcaSlicer.Worker`: after binary detection, compare `OrcaBinaryDetector.GetVersionAsync()` against configured `Worker:EngineVersion` / `SlicerRegistry:Version`. On mismatch → log error, fail readiness, refuse to advertise the `orcaslicer:<version>` tag. Closes the stub-substitution and bad-build-arg gaps.

### 6. Docker (authoritative sources under `scripts/docker/`)
- The compose generator in `scripts/` merges named YAML addons via env flags (no docker-compose `profiles:` support). Add:
  - `scripts/docker/compose/docker-compose.orcaslicer-worker-previous.yml` addon with the second worker service, distinct env vars (`SlicerRegistry__Host`, `Worker__WorkerId`, `Worker__InstanceId`, `SlicerRegistry__ServiceName`, its own worker cache volume), and its own image built with `ORCASLICER_VERSION=<previous>`.
  - New env flag `ENABLE_ORCA_WORKER_PREVIOUS` (default off) so single-engine deployments remain the default and existing users see no change.
- `Dockerfile.base-orcaslicer-binaries` and `Dockerfile.multistage` accept the same `ORCASLICER_VERSION` build-arg they already do — reused verbatim by a second `docker build`; no reusable-stage refactor, no `IMAGE_TAG_SUFFIX` scheme.
- The `ORCASLICER_VERSION` build-arg is already `ENV`-bound to `SlicerRegistry__Version` inside the image, so image identity stays coherent with advertised version.
- Nginx routes unchanged (workers pull; they do not accept slice traffic).
- arm64: both Orca versions ship x86_64-only AppImages; arm64 hosts run under emulation as today. Document in orcaslicer versioning notes that dual-engine doubles emulation load on arm64.

### 7. Frontend (React)
- Slice wizard adds a "Slicer version" combobox next to engine selector; populated from extended `/api/slicers/engines` (`availableVersions`). Default = latest. Persist on submit as `slicerEngineVersion`. Applies to both entry points (`NewSliceJobPage` and `QuickSliceModal`).
- Profile / settings / icon renderers scope to `(engine, version)` via the new `?version=` query param.
- iOS mobile: no immediate mobile change; contract is additive (`slicerEngineVersion` optional). Track as follow-up if operators want mobile version selection.

### 8. Lifecycle policy
- Documented in an existing doc (extending, not creating): `docs/orcaslicer-versioning.md` (or the closest existing doc). Rule: keep current + previous, drop oldest on next Orca bump. Drop step is the same plugin-shipping checklist as add (§1) + remove the previous-worker compose flag / retag current → previous.
- Old jobs carrying a retired `SlicerEngineVersion` string remain valid (informational, no FK).

### 9. Explicit non-goals for this PR
- No changes to `SlicerEngineType` enum semantics.
- No changes to PrusaSlicer wiring.
- No changes to AppDbContext or `Farm.Migrations.*`.
- No touching #741.
- No claim-time version resolution.
- No dead-code changes to `SlicerOrchestrator.SubmitJobAsync` (legacy path).

## Test plan (minimum)
1. Live path: versioned job is NOT claimed by a wrong-version worker (guards OR-match bug) — highest priority.
2. NULL-version job claimable by any worker (back-compat).
3. `QueueConsumerService.GetWorkerCapabilities()` and `SlicerRegistrationClient` both emit `orcaslicer:<version>` from config; the two sources agree.
4. Registry non-empty in `Farm.Slicer.Host` (integration, microservices mode) — not just monolith/unit.
5. Startup version cross-check fails readiness on binary/declared mismatch.
6. Two workers with distinct Host/InstanceId/WorkerId register as two Worker rows (no clobber on `SlicersService` upsert).
7. Migrations present for both PG and SQL Server providers (`has-pending-model-changes` clean); NULL and non-NULL round-trip.
8. Docker: previous-worker image ENV `SlicerRegistry__Version` matches baked-in `ORCASLICER_VERSION` build arg.
9. React version selector renders, defaults to latest, sends `slicerEngineVersion` in POST body from both entry points.
10. `/api/slicers/engines` returns `availableVersions[]` populated from the registry.

## Verdict
Proceed with implementation on this branch, gating PR on unanimous 3-way review (Bishop opus 4.8 max, Hicks gpt-5.6-sol max, Vasquez gemini-3.1-pro-preview high).
