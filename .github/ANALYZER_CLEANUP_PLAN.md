# Analyzer cleanup plan (meta-issue)

This document tracks incremental cleanup of analyzer warnings across the solution. The goal is to reduce noise without risky churn, landing small, reviewable changes in phases.

Status baseline (from latest local build/tests):
- Build: succeeded with ~193 warnings (API), tests add a handful more
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
- [ ] S4136: Co-locate overloads (method overload adjacency) in flagged classes (Moonraker/Prusa/Harvest/PrinterClientBase)
- [x] CA1849: Prefer `RunAsync` over `Run` in `Program.cs`
- [ ] CA1805: Remove explicit default initializers where redundant (Entities and services)

Acceptance criteria:
- [x] Build still succeeds
- [x] Tests remain green
- [ ] Warning count reduced by at least 30–50 without public API breaks

## Phase 2 — Input validation and API surface polish
- [ ] CA1062: Add guard clauses or nullable annotations + [ApiController] conventions to avoid null-use in controllers/services
- [ ] S6965/S6968: Ensure controller actions have HTTP verb attributes and ProducesResponseType annotations for success paths
- [ ] CA1034: Move public nested request/response types in controllers to top-level DTOs (e.g., `PrintersController` nested types)
- [ ] CA3003: Mitigate file path injection warnings in `GcodeLibraryController` by validating and constraining paths

Acceptance criteria:
- [ ] Build succeeds, tests green
- [ ] API Swagger/metadata reflects accurate response types
- [ ] Null handling covered by integration tests for at least 2 representative endpoints

## Phase 3 — URL/URI migration (gradual)
- [ ] CA1055/CA1056: Introduce Uri-typed accessors alongside existing string properties (non-breaking) in DTOs/entities where feasible
- [ ] Internally adopt Uri for HTTP calls and camera URL normalization; keep string properties for serialization until clients are updated
- [ ] Add custom JSON converter if needed for smoother string<->Uri interop

Acceptance criteria:
- [ ] No public breaking changes without an adapter
- [ ] All internal HTTP and URL logic uses Uri
- [ ] Camera URL normalization and outbound calls validated by tests

## Phase 4 — Exceptions, logging, and disposals
- [ ] CA2201/S112: Replace `throw new Exception(...)` with specific exception types; narrow catches
- [ ] Adopt LoggerMessage pattern for hot paths (controllers frequently hit, network clients, background services)
- [ ] IDISP013/CA2000: Ensure `await using` and disposal correctness in Moonraker/Prusa/SDCP clients and background services

Acceptance criteria:
- [ ] No broad catch-all except where required, with logging
- [ ] No disposable leaks flagged by analyzers in the touched areas

## Phase 5 — Model and naming cleanups
- [ ] CA2227: Make collection properties read-only (init/private set) in models that do not require external mutation
- [ ] CA1711/CA1724: Evaluate renames for types ending with Queue and name conflicts (e.g., Storage); prefer internal or file-scoped suppressions if rename is breaking

Acceptance criteria:
- [ ] Either rename with migration notes or add targeted suppressions with justification

## Nice-to-haves (deferred if risky)
- [ ] Expand use of nullable reference types annotations (enable in projects or tighten per-file)
- [ ] Method-level cancellation support review (ensure ct is passed to async I/O)

## Tracking (proposed PRs)
Create small PRs referencing this meta-issue, one per bullet or tight cluster:
- [x] PR: Phase 1 hygiene batch A (JsonSerializerOptions + RunAsync + trivial Sonar fixes)
- [ ] PR: Phase 1 hygiene batch B (overload adjacency + default initializers)
- [ ] PR: Phase 2 input validation (null guards + ProducesResponseType)
- [ ] PR: Phase 3 Uri migration (internal first, adapters for DTOs)
- [ ] PR: Phase 4 exceptions/logging/disposals
- [ ] PR: Phase 5 model/naming, with suppressions where breaking

Notes:
- Re-run end-to-end validations per repository instructions after each PR (build, test, manual health checks).
- Prefer adding unit/integration tests alongside changes that affect behavior.
