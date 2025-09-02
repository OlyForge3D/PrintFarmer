# Analyzer cleanup plan (meta-issue)

This document tracks incremental cleanup of analyzer warnings across the solution. The goal is to reduce noise without risky churn, landing small, reviewable changes in phases.

Status baseline (from latest local build/tests):
- Build: succeeded with 6 warnings (reduced from ~176 in early passes), tests all green
- Framework: .NET 9, ASP.NET Core API + Blazor WASM

## Principles
- Prefer small PRs by rule-family or local feature area
- Avoid public API breaks unless value is high and risk is low; otherwise, add adapters or migrate gradually
- Add targeted tests when changing behavior
- Cache and reuse objects (e.g., JsonSerializerOptions, HttpClient)

## Phase 1 — Low-risk hygiene and correctness
- [x] CA1869: Cache JsonSerializerOptions in: `GlobalExceptionMiddleware`, `Program`, `PresetService`, `NetworkDiscoverySettingsService` (static/shared instances)
- [x] CA1513: Use ObjectDisposedException.ThrowIfDisposed in `InMemoryHarvestQueue`
- [ ] S1481/S1066/S3626/S1905: Remove unused locals, merge redundant ifs, remove redundant jumps/casts in the flagged files
- [x] S6580: Provide CultureInfo when parsing/formatting DateTime in `PrintersController`, `SpoolmanService`
- [ ] S6605/S6602: Prefer collection-specific Exists/Array.Find where suggested in tests and validators
- [x] S4136: Co-locate overloads (method overload adjacency) in flagged classes (PrusaLinkApiClient.GetVersionAsync)
- [x] CA1849: Prefer `RunAsync` over `Run` in `Program.cs`
- [x] S2325: Make methods static when possible (HarvestWorkerService.ExtractMetadataAsync)
- [x] S3923: Simplify redundant conditional logic (NetworkDiscoveryService.GetHostsInRange)
- [x] S1199: Reduce nesting by extracting method from complex block (MoonrakerSubscriptionService.EnumerateAndStartSubscriptionsAsync)
- [ ] CA1805: Remove explicit default initializers where redundant (Entities and services)

Acceptance criteria:
- [x] Build still succeeds
- [x] Tests remain green
- [ ] Warning count reduced by at least 30–50 without public API breaks

## Phase 2 — Input validation and API surface polish
- [x] CA1034: Move public nested request/response types in controllers to top-level DTOs
	- Moved from `PrintersController` to:
		- `api/Controllers/Requests/StartPrintRequest.cs`
		- `api/Controllers/Requests/UploadGcodeRequest.cs` (kept for future use)
		- `api/Controllers/Responses/CameraUrlResult.cs`
- [x] S6965/S6968: Add ProducesResponseType annotations and ensure HTTP verb attributes
	- `PrintersController`: test/demo endpoints annotated; camera URL annotated
	- `GcodeLibraryController`: all CRUD and download endpoints annotated
	- `MoonrakerDiagnosticsController`: roots/directory/filelist annotated
	- Remaining: some `PrintersController` operational endpoints (camera enable/disable, file ops) and other controllers (e.g., `GcodeHarvestController`)
- [x] CA1062: Add guard clauses in representative endpoints
	- `PrintersController.ResolveHostAsync` and `StartPrintAsync`
	- `GcodeLibraryController.UploadFileAsync` and `UpdateFileAsync`
	- Remaining: sweep other controllers for request-body nulls
- [x] CA3003: Mitigate file path injection in `GcodeLibraryController`
	- Constrain to webroot `gcode-library` via `GetFullPath` + prefix check
	- Download/Delete now validate and operate only under library root
	- Upload saves with generated filename and validated extension
- [x] Replace Console.WriteLine in API with structured logging in hot/demo paths

Acceptance criteria:
- [x] Build succeeds, tests green
- [x] API Swagger/metadata reflects accurate response types for updated endpoints
- [ ] Null handling covered by integration tests for at least 2 representative endpoints (follow-up)

Acceptance criteria:
- [ ] Build succeeds, tests green
- [ ] API Swagger/metadata reflects accurate response types
- [ ] Null handling covered by integration tests for at least 2 representative endpoints

## Phase 3 — URL/URI migration (gradual)
- [x] CA1055/CA1056: Introduce Uri-typed accessors alongside existing string properties (non-breaking) in DTOs/entities where feasible (added to PrinterDto, PrinterBasicDto, PrinterStatusDto, SpoolmanConfigDto)
- [ ] Internally adopt Uri for HTTP calls and camera URL normalization; keep string properties for serialization until clients are updated
- [ ] Add custom JSON converter if needed for smoother string<->Uri interop

Acceptance criteria:
- [ ] No public breaking changes without an adapter
- [ ] All internal HTTP and URL logic uses Uri
- [ ] Camera URL normalization and outbound calls validated by tests

## Phase 4 — Exceptions, logging, and disposals
- [ ] CA2201/S112: Replace `throw new Exception(...)` with specific exception types; narrow catches
	- Partial: `MoonrakerDiagnosticsController` now returns Problem responses instead of throwing general exceptions
- [ ] Adopt LoggerMessage pattern for hot paths (controllers frequently hit, network clients, background services)
- [x] IDISP013: Ensure `await using` and disposal correctness in MoonrakerSubscriptionService (narrow suppression with async scope)
- [x] CA2254: Use templated logging messages (AppSettings configuration validation)

Acceptance criteria:
- [ ] No broad catch-all except where required, with logging
- [ ] No disposable leaks flagged by analyzers in the touched areas

## Phase 5 — Model and naming cleanups
- [ ] CA2227: Make collection properties read-only (init/private set) in models that do not require external mutation
- [x] CA1711: Scoped suppression added for IHarvestQueue and InMemoryHarvestQueue (avoid breaking rename)
- [x] CA1724: Class-level suppression on PrusaLinkModels.Storage to avoid conflict without rename
- [x] S3260: Mark private classes as sealed (NetworkDiscoveryService.PrinterInfo, GcodeHarvestService.PrinterFileInfo)
- [x] S125: Remove commented-out dead code (DatabaseInitializer)
- [x] CA1002: Shift API-facing return types to IReadOnlyList where safe (INetworkDiscoverySettingsService.GetDynamicNetworkRanges)

Acceptance criteria:
- [ ] Either rename with migration notes or add targeted suppressions with justification

## Nice-to-haves (deferred if risky)
- [ ] Expand use of nullable reference types annotations (enable in projects or tighten per-file)
- [ ] Method-level cancellation support review (ensure ct is passed to async I/O)

## Tracking (proposed PRs)
Create small PRs referencing this meta-issue, one per bullet or tight cluster:
- [x] PR: Phase 1 hygiene batch A (JsonSerializerOptions + RunAsync + trivial Sonar fixes)
- [x] PR: Phase 1 hygiene batch C (S2325/S3923/S1199 in services)
- [ ] PR: Phase 1 hygiene batch B (overload adjacency + default initializers)
- [ ] PR: Phase 2 input validation (null guards + ProducesResponseType)
- [x] PR: Phase 3 Uri migration (step 1: non-breaking DTO accessors)
- [ ] PR: Phase 3 Uri migration (step 2: internal adoption + optional JSON converter)
- [ ] PR: Phase 4 exceptions/logging/disposals
- [ ] PR: Phase 5 model/naming, with suppressions where breaking

## Current remaining warnings of note (snapshot)
- CA1711: Naming for types ending with Queue (evaluate rename vs. scoped suppression)
- CA1724: Type name conflicts with namespaces (e.g., Storage) — evaluate mitigation
- S6960: Consider splitting controller responsibilities (review scope/benefit)
- S6964: Clarify binding on remaining POST/PUT endpoints (partial; continue sweep)

## Remaining items snapshot (by rule and location)

Phase 2 high-priority targets next:
- S6965/S6968 (controller metadata):
	- PrintersController: add ProducesResponseType to camera enable/disable, file list/upload/print-from-file endpoints
	- GcodeHarvestController: add ProducesResponseType on key actions (e.g., update/delete, download)
- CA1062 (null-guards):
	- Sweep remaining controllers for request-body nulls (e.g., harvest endpoints)
- CA3003 (path validation):
	- Verify remaining file IO paths outside `GcodeLibraryController` (if any)

Lower-priority or later phases (not tackled next):
- CA2227 read-only collections in models (Phase 5)
- LoggerMessage expansion across services (Phase 4)
- Uri-typed models internal adoption + optional converter (Phase 3 step 2)

Notes:
- Re-run end-to-end validations per repository instructions after each PR (build, test, manual health checks).
- Prefer adding unit/integration tests alongside changes that affect behavior.
