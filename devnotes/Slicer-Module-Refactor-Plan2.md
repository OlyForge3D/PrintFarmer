# Slicer Plugin Architecture Plan

> **Updated 2026-02-19** — Reflects actual implementation state after Phases 1-4.

## Problem Statement

Slicing, 3D model uploads, and file library functionality are currently tightly wired into the API project. The goal is to refactor these into a **self-contained plugin system** (modeled after the existing backend plugin pattern) so that:

1. **Slicing can be completely removed** — the API builds and runs without slicer code, controllers, SignalR hubs, or related features
2. **Modules can be re-added with zero API changes** — drop plugin DLLs into the output directory and they self-register via assembly scanning
3. **Each slicer engine is its own plugin** — OrcaSlicer and PrusaSlicer are independent, removable modules

## Actual Architecture (as implemented)

```
Farm.Web.Api
  ├── Farm.Slicer.Module.Api      (controllers, hubs, API-level services)
  │     └── Farm.Slicer.Module    (domain, DbContext, repositories, business logic)
  │           ├── Farm.Slicer.Plugin.Core  (contracts: interfaces, records, attribute)
  │           └── Farm.Infrastructure      (shared infra, AppDbContext)
  │
  ├── Farm.Slicers.OrcaSlicer.v2_3_1  (compile-time ref; will become runtime-only)
  │     └── Farm.Slicer.Plugin.Core    (contracts only — no Module dependency)
  │
  └── Farm.Slicer.Host            (standalone host for slicer microservice deployment)
        └── Farm.Slicer.Module + Farm.Slicer.Module.Api
```

**Key differences from original plan:**
- Named `Farm.Slicer.Module` / `Farm.Slicer.Module.Api` instead of `Farm.Slicer.Integration`
  (pragmatic: avoids renaming everything; the Module + Module.Api pair is well-established).
- OrcaSlicer is currently loaded via both compile-time ProjectReference AND runtime discovery
  capability. Phase 5 removes the compile-time reference.
- `Farm.Slicer.Plugin.Core` preserves the existing `Farm.Slicer.Module.Contracts.Libraries`
  namespace to minimize consumer changes.

## Architecture Layers

### Layer 1: `Farm.Slicer.Plugin.Core` (Contract Layer)

Zero dependencies on API or Infrastructure. Defines:

- **`ISlicerPlugin`** — base plugin interface (analogous to `IBackendClientPlugin`)
  - `SlicerType` (string identifier, e.g., "orcaslicer")
  - `DisplayName`, `Description`, `Version`
  - `RegisterServices(IServiceCollection)` — plugin's DI registrations
  - `GetCapabilities()` — what this slicer supports (profiles, bundles, etc.)

- **`ISlicerPluginRegistry`** — thread-safe registry of discovered plugins (analogous to `IBackendPluginRegistry`)

- **`[SlicerPluginAttribute]`** — assembly-level marker for plugin discovery

- **Shared contracts:**
  - `ISlicerProfilesProvider` — expose profiles from a slicer
  - `ISlicingEngine` — execute slicing operations
  - `ISlicerAssetProvider` — bed textures, printer covers
  - `ISlicerUIMetadata` — UI-facing metadata (display name, icon, supported file types)
  - Shared DTOs for slice jobs, profiles, results

### Layer 2: Concrete Slicer Plugins

Each plugin project (e.g., `Farm.Slicer.Plugin.OrcaSlicer`) references only `Farm.Slicer.Plugin.Core` and implements:

- The `ISlicerPlugin` interface
- Slicer-specific clients, services, and pipeline logic
- Worker communication (registration, heartbeat, job dispatch)
- Profile discovery and caching

### Layer 3: `Farm.Slicer.Integration` (API Bridge)

This is the **optional integration library** that wires slicer plugins into the API. It:

- Provides controllers (slice jobs, profiles, file management)
- Provides SignalR hub (`SlicerHub`)
- Provides the assembly scanning / plugin loader (`SlicerPluginExtensions`)
- Registers itself conditionally — if no slicer plugins found, nothing is registered
- References `Farm.Slicer.Plugin.Core` but NOT concrete plugins

**Key insight:** The API project references `Farm.Slicer.Integration` (which is lightweight). The integration library discovers concrete plugins at runtime. Removing the integration library DLL removes ALL slicer functionality.

## Dependency Graph

```
Farm.Web.Api
  ├── Farm.Slicer.Integration (optional — API bridge)
  │     └── Farm.Slicer.Plugin.Core (contracts only)
  │
  ├── Farm.Slicer.Plugin.OrcaSlicer (runtime discovery, not project ref)
  │     └── Farm.Slicer.Plugin.Core
  │
  └── Farm.Slicer.Plugin.PrusaSlicer (runtime discovery, not project ref)
        └── Farm.Slicer.Plugin.Core
```

## Implementation Status

### Phase 1: Scaffolding — ✅ COMPLETED (PFarm1-2ni.1)

Scaffolded `Farm.Slicer.Module`, `Farm.Slicer.Module.Api`, and `Farm.Slicer.Host`.
Moved domain entities, DbContext, and repositories out of the API.
Created `AddSlicerModule()` / `AddSlicerControllers()` / `MapSlicerHubs()` entry points.

### Phase 2: Entity Migration — ✅ COMPLETED (PFarm1-2ni.2)

Moved all slicer domain entities from `Farm.Infrastructure` into `Farm.Slicer.Module.Domain`.
Separated `SlicerDbContext` from `AppDbContext`.
Resolved circular dependencies between modules.

### Phase 3: Decoupling — ✅ COMPLETED (PFarm1-2ni.3)

Broke remaining circular references. Moved services, controllers, SignalR hub, and background
services into the slicer module assembly. Addressed first code-review findings (11 items).
Committed as `3373ae96` on `feat/modularization`.

### Phase 4: Hardening & Plugin Contracts — ✅ COMPLETED (PFarm1-2ni.4)

| Bead | Description | Status |
|------|-------------|--------|
| 4.1 | Fix `Slicer:Enabled` default comment mismatch | ✅ |
| 4.2 | Remove duplicate `StaleWorkerCleanupSettings` registration | ✅ |
| 4.3 | Provider-aware DB init (SQLite → EnsureCreated, others → MigrateAsync) | ✅ |
| 4.4 | Idempotency guard on `AddSlicerModule` | ✅ |
| 4.5 | Extract shared `DatabaseProviderConfiguration` | ✅ |
| 4.6 | Fix `DbContextFactory` options drift | ✅ |
| 4.7 | Catch-all disabled-mode middleware for all slicer route prefixes | ✅ |
| 4.8 | `SlicerDisabledIntegrationTests` (11 tests, dedicated factory) | ✅ |
| 4.9 | `ProfileTaskCheckServiceTests` (12 tests) | ✅ |
| 4.10 | Extract `Farm.Slicer.Plugin.Core` contract library | ✅ |
| 4.11 | Runtime DLL plugin discovery (`LoadPluginAssemblies`) | ✅ |
| 4.12 | Document Phase 6 file library decision | ✅ |
| 4.13 | Update this plan document | ✅ |

**Key artifacts created in Phase 4:**  
- `Farm.Slicer.Plugin.Core` — standalone contract assembly (interfaces, records, attribute)
- `DatabaseProviderConfiguration` — shared DB provider resolution (eliminates duplication)
- `SlicerDisabledWebApplicationFactory` — env-var-based test factory for disabled-mode tests
- `SlicerPluginDiscovery.LoadPluginAssemblies()` — loads DLLs from `Slicer:PluginsPath` at startup

### Phase 5: Clean Up API (FUTURE)

- Remove `Farm.Slicers.OrcaSlicer.v2_3_1` ProjectReference from `Farm.Web.Api.csproj`
- Configure `Slicer:PluginsPath` in production Docker to point to `/app/plugins/`
- Add post-build copy step for slicer engine DLLs to plugins directory
- Verify API works with zero plugins (already covered by disabled tests)

### Phase 6: File Library Decision — ✅ DOCUMENTED

**Decision: (b) — Separate module.**

The 3D model file library (uploads, folders, tagging) exists independently of slicing.
Users may upload and organize STL/3MF files even without a slicer engine. The file library
should remain accessible when `Slicer:Enabled=false`, similar to how print job management
works without a specific printer backend.

**Implications:**
- File library routes (`/api/3d-models`, `/api/3d-models/folders`, `/api/3d-models/query`)
  are currently served as empty-result stubs when slicer is disabled. These should be migrated
  to a `Farm.FileLibrary.Module` in a future phase.
- Slicing-specific routes (profiles, workers, slice jobs) correctly return SLICER_DISABLED 404.
- The catch-all middleware already distinguishes between file library paths (stubs) and slicer
  paths (404 with structured error), providing the right user experience for both cases.

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Discovery mechanism | Runtime DLL scanning | Matches backend plugin pattern; enables deployment-time toggling |
| Plugin granularity | One plugin per slicer engine | Each engine is independently deployable |
| Integration layer | Separate project (`Farm.Slicer.Integration`) | Keeps API clean; single removal point |
| Worker communication | Remains HTTP/Redis-based | Workers are separate processes; plugin controls registration protocol |
| Profile storage | Plugin-owned | Each slicer manages its own profile format and storage |
| Shared DTOs | In `Farm.Slicer.Plugin.Core` | Prevents API dependency on concrete plugins |

## Risks & Mitigations

- **Risk:** Moving controllers/hubs out of API may break routing.
  **Mitigation:** Use `ApplicationPart` to load controllers from external assemblies — ASP.NET Core supports this natively.

- **Risk:** SignalR hub conditional registration is complex.
  **Mitigation:** Map hub endpoint only when integration library is present; use `IEndpointRouteBuilder` extension method from integration project.

- **Risk:** Frontend expects slicer endpoints to always exist.
  **Mitigation:** Frontend should handle 404/503 gracefully for slicer features; show "Slicing not available" when endpoints are missing.

## Notes

- The existing `SlicerPluginAttribute` and `ISlicerLibrary` pattern in `Farm.Slicers.OrcaSlicer.v2_3_1` is a starting point but is limited to library/profile metadata. The new system extends this to full lifecycle management (DI, controllers, hubs, background services).
- Worker projects (`orcaslicer-worker`, `prusaslicer-worker`) remain separate deployable services. The plugin system manages the API-side integration, not the worker process itself.
- This pattern can be replicated for other feature modules (notifications, file library, etc.) in the future.
