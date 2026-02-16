# Slicer Plugin Architecture Plan

## Problem Statement

Slicing, 3D model uploads, and file library functionality are currently tightly wired into the API project. The goal is to refactor these into a **self-contained plugin system** (modeled after the existing backend plugin pattern) so that:

1. **Slicing can be completely removed** — the API builds and runs without slicer code, controllers, SignalR hubs, or related features
2. **Modules can be re-added with zero API changes** — drop plugin DLLs into the output directory and they self-register via assembly scanning
3. **Each slicer engine is its own plugin** — OrcaSlicer and PrusaSlicer are independent, removable modules

## Proposed Approach

Mirror the proven `Farm.Backend.Plugin.*` pattern:

```
Farm.Slicer.Plugin.Core         → Interfaces, attributes, registry (no implementation)
Farm.Slicer.Plugin.OrcaSlicer   → OrcaSlicer-specific implementation
Farm.Slicer.Plugin.PrusaSlicer  → PrusaSlicer-specific implementation
Farm.Slicer.Integration         → API integration layer (controllers, hubs, services)
```

**Discovery flow:** API startup → scan for `Farm.Slicer.Plugin.*.dll` → find `[SlicerPluginAttribute]` → instantiate `ISlicerPlugin` → call `RegisterServices(IServiceCollection)` → slicer features available.

**When no plugins found:** Slicer-related API endpoints return 404 or are not registered. SignalR hub is not mapped. No slicer background services run.

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

## Implementation Todos

### Phase 1: Core Contracts

- **slicer-plugin-core-interfaces** — Create `Farm.Slicer.Plugin.Core` project with `ISlicerPlugin`, `ISlicerPluginRegistry`, `SlicerPluginAttribute`, and shared DTOs/contracts. Model after `Farm.Backend.Plugin.Core` pattern. No dependencies on Infrastructure or API.

- **slicer-plugin-registry** — Implement `SlicerPluginRegistry` (thread-safe singleton) with `Register()`, `GetPlugin()`, `GetAllPlugins()`, `IsRegistered()`. Mirror `BackendPluginRegistry`.

### Phase 2: Integration Layer

- **slicer-integration-project** — Create `Farm.Slicer.Integration` project. Move slicer controllers, SignalR hub, orchestration services, and job queue logic from `Farm.Web.Api` into this project. Expose a single `AddSlicerIntegration(IServiceCollection)` extension method.

- **slicer-plugin-discovery** — Implement `SlicerPluginExtensions` assembly scanning: load `Farm.Slicer.Plugin.*.dll`, find `[SlicerPluginAttribute]`, instantiate `ISlicerPlugin`, register in `SlicerPluginRegistry`, call `RegisterServices()`. Model after `BackendPluginExtensions`.

- **conditional-registration** — Ensure controllers, hubs, and background services are only registered when at least one slicer plugin is discovered. API must build and run cleanly with zero slicer plugins.

### Phase 3: Extract OrcaSlicer Plugin

- **orcaslicer-plugin** — Create `Farm.Slicer.Plugin.OrcaSlicer` project. Move OrcaSlicer-specific code from `Farm.Slicers.OrcaSlicer.v2_3_1` and `orcaslicer-worker` shared logic. Implement `ISlicerPlugin`. Remove direct project references from API.

- **orcaslicer-profiles** — Implement `ISlicerProfilesProvider` for OrcaSlicer, including worker-based profile discovery and caching.

### Phase 4: Extract PrusaSlicer Plugin

- **prusaslicer-plugin** — Create `Farm.Slicer.Plugin.PrusaSlicer` project. Move PrusaSlicer-specific code. Implement `ISlicerPlugin`.

### Phase 5: Clean Up API

- **remove-slicer-coupling** — Remove all direct slicer references from `Farm.Web.Api.csproj`. Verify API builds and runs with:
  a. No slicer plugins (graceful degradation)
  b. Only OrcaSlicer plugin
  c. Only PrusaSlicer plugin
  d. Both plugins

- **update-tests** — Update or create tests verifying:
  - Plugin discovery finds/ignores plugins correctly
  - API works with zero plugins (no 500 errors)
  - Each plugin registers its services correctly
  - Slice job routing works with multiple plugins

### Phase 6: Related Features (3D File Library)

- **file-library-extraction** — Evaluate whether 3D model uploads and file library should be:
  a. Part of the slicer integration layer (removed with slicers)
  b. Their own separate module (can exist without slicers)
  - If (a): Move into `Farm.Slicer.Integration`
  - If (b): Create a separate `Farm.FileLibrary.Integration` following the same pattern

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
