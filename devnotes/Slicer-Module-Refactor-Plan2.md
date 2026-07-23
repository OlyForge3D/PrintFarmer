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

### Phase 5: Slicer Integration Shim — BEAD PFarm1-2ni.5 (P1)

**Strategy: minimal compile-time shim, runtime everything else.**

```
API (compile-time) → Farm.Slicer.Integration (thin shim, no EF/SignalR/controllers)
                         ↓ (compile-time)
                     Farm.Slicer.Plugin.Core (contracts only)
                         ↓ (runtime DLL load from Slicer:PluginsPath)
                     Farm.Slicer.Module.dll
                     Farm.Slicer.Module.Api.dll
                     Farm.Slicers.OrcaSlicer.v2_3_1.dll
                     Farm.Slicer.Migrations.PostgreSQL.dll
                     Farm.Slicer.Migrations.SqlServer.dll
```

**Sub-beads:**

| Bead | Work |
|------|------|
| PFarm1-2ni.5.1 | Add `ISlicerModule` + `ISlicerHubRegistrar` interfaces to `Farm.Slicer.Plugin.Core` |
| PFarm1-2ni.5.2 | Implement both interfaces in `Farm.Slicer.Module` and `Farm.Slicer.Module.Api` |
| PFarm1-2ni.5.3 | Create `Farm.Slicer.Integration` shim (load DLLs → ApplicationParts → call contracts) |
| PFarm1-2ni.5.4 | Remove 4 slicer ProjectReferences from `Farm.Web.Api.csproj`; wire shim in `Program.cs` |
| PFarm1-2ni.5.5 | Add `Slicer:PluginsPath` to appsettings + MSBuild AfterBuild copy step for plugin DLLs |
| PFarm1-2ni.5.6 | Update `CustomWebApplicationFactory` + verify all tests pass |

**Key contracts (all in `Farm.Slicer.Plugin.Core`):**
- `ISlicerModule` — `void RegisterServices(IServiceCollection, IConfiguration)` — called by shim after DLL load
- `ISlicerHubRegistrar` — `void MapHubs(IEndpointRouteBuilder)` — called by shim's `MapSlicerIntegrationHubs()`
- SignalR hubs: `ApplicationPart` auto-discovers controllers; hubs require the `ISlicerHubRegistrar` interface call

**Related: Backend Plugin Runtime Loading — BEAD PFarm1-e4i (P2)**
Same pattern for the 5 concrete backend plugins (Moonraker, PrusaLink, Sdcp, OctoPrint, FlashForge).
`BackendPluginLoader` infrastructure already exists; just needs wiring + ProjectReference removal.
Sub-beads: PFarm1-e4i.1 (wire loader), PFarm1-e4i.2 (remove refs), PFarm1-e4i.3 (MSBuild copy), PFarm1-e4i.4 (tests).

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

## SimplyPrint Parity Gap Inventory (PFarm1-zlbo.13)

Scope and evidence used:
- SimplyPrint artifacts: [simplyprint-dialog-text.txt](../simplyprint-dialog-text.txt), [simplyprint-live-slicer.yml](../simplyprint-live-slicer.yml), [simplyprint-canvas-inspection.json](../simplyprint-canvas-inspection.json)
- Current PFarm1 slicer UI: [src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx](../src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx), [src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx](../src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx), [src/Web/ReactApp/src/features/slicer/components/settings/MachineProfileEditor.tsx](../src/Web/ReactApp/src/features/slicer/components/settings/MachineProfileEditor.tsx), [src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx](../src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx), [src/Web/ReactApp/src/features/slicer/components/viewer/SlicerWorkspace.tsx](../src/Web/ReactApp/src/features/slicer/components/viewer/SlicerWorkspace.tsx), [src/Web/ReactApp/src/features/slicer/components/viewer/SlicerToolbar.tsx](../src/Web/ReactApp/src/features/slicer/components/viewer/SlicerToolbar.tsx), [src/Web/ReactApp/src/features/slicer/components/viewer/SlicerLeftTools.tsx](../src/Web/ReactApp/src/features/slicer/components/viewer/SlicerLeftTools.tsx)
- Bead metadata for PFarm1-zlbo.13/.14/.15/.16/.5 was not found in local issue snapshots; mapping below is implementation guidance and must be validated against live bead text (Needs live confirm).

### Side-by-Side Gap Inventory

| Area | SimplyPrint observed | PFarm1 current | Gap inventory | Priority | Evidence |
|---|---|---|---|---|---|
| Process | Basic/Simple/Advanced + category tabs; extensive quality stack including Wall generator, Walls and surfaces, Flow ratio, Bridging, Overhangs. | Core structure exists with Basic/Simple/Advanced and tabs. Several sections implemented (Layer height, Line width, Seam, Precision, Speed, Support, Temperature/Retraction/Cooling/Ironing). | 1) Multimaterial is placeholder only. 2) Explicit first-class groups/controls for Wall generator, Walls and surfaces, Flow ratio, Bridging, Overhangs are missing in typed UI. 3) Settings search field parity is missing. | P0 (multimaterial + missing core groups), P1 (search UX) | [simplyprint-dialog-text.txt](../simplyprint-dialog-text.txt), [src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx#L903](../src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx#L903), [src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx#L430](../src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx#L430) |
| Machine | Machine profile select + explicit edit affordance in slicer dialog. | Machine profile select + edit button + machine editor modal exists. | No high-confidence P0 machine blocker from provided artifacts. Remaining machine-depth parity against SimplyPrint internals cannot be validated from current artifact set (Needs live confirm). | P2 | [simplyprint-live-slicer.yml#L65](../simplyprint-live-slicer.yml#L65), [src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx#L1025](../src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx#L1025), [src/Web/ReactApp/src/features/slicer/components/settings/MachineProfileEditor.tsx](../src/Web/ReactApp/src/features/slicer/components/settings/MachineProfileEditor.tsx) |
| Filament | Filaments panel includes add extruder (+), active extruder context, add filament flow, quick temp context, and profile edit path. | Filament flow is material + profile select, with edit modal support. | Missing multi-extruder filament lane UX (add extruder / per-extruder active profile context) in the primary slicer page flow. | P0 | [simplyprint-dialog-text.txt](../simplyprint-dialog-text.txt), [simplyprint-live-slicer.yml#L82](../simplyprint-live-slicer.yml#L82), [src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx#L1062](../src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx#L1062), [src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx](../src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx) |
| Workspace | WebGL canvas workspace + broad tool rail: Add object, Add plate, Arrange, Lay on side, Color painting, Support painting, Seam painting, Fuzzy skin painting, Add text, Measure distance, Smart rotate, Show history, Snap settings. | Slicer workspace exists with WebGL bed and tooling, but left tool rail is limited to move/rotate/scale/layers and top toolbar only covers a subset of actions. Workspace is STL-only in NewSliceJobPage path. | 1) Missing workspace tools: Add plate, Color painting, Fuzzy skin painting, Add text, Smart rotate, Show history, Snap settings. 2) Workspace not available for non-STL model types in slice flow. | P0 (tooling parity), P1 (non-STL workspace parity) | [simplyprint-live-slicer.yml#L105](../simplyprint-live-slicer.yml#L105), [simplyprint-canvas-inspection.json#L15](../simplyprint-canvas-inspection.json#L15), [src/Web/ReactApp/src/features/slicer/components/viewer/SlicerLeftTools.tsx#L14](../src/Web/ReactApp/src/features/slicer/components/viewer/SlicerLeftTools.tsx#L14), [src/Web/ReactApp/src/features/slicer/components/viewer/SlicerToolbar.tsx](../src/Web/ReactApp/src/features/slicer/components/viewer/SlicerToolbar.tsx), [src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx#L879](../src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx#L879) |

### Priority Ranking (P0/P1/P2)

| Priority | Missing groups / controls |
|---|---|
| P0 | Process: Multimaterial implementation and typed parity for Wall generator / Walls and surfaces / Flow ratio / Bridging / Overhangs. Filament: multi-extruder lane UX (+ add extruder + per-extruder profile context). Workspace: missing major tools (Add plate, Color painting, Fuzzy skin painting, Add text, Smart rotate, Show history, Snap settings). |
| P1 | Process: settings search UX parity. Workspace: non-STL route should stay in slicer workspace path for operational consistency. |
| P2 | Machine-depth parity verification and any remaining machine-specific controls not visible in current artifact set (Needs live confirm). |

### Gap-to-Bead Mapping (.14/.15/.16 + dependency .5)

Bead details were not resolvable in local issue snapshots; map below is the recommended implementation split and should be reconciled to live bead text (Needs live confirm).

| Bead | Implement first | Why first |
|---|---|---|
| PFarm1-zlbo.14 | Process + Filament P0: Multimaterial tab implementation; typed Process sections for Wall generator/Walls+surfaces/Flow ratio/Bridging/Overhangs; multi-extruder filament lane UI (+ add extruder, per-lane profile/temp context). | Highest user-visible settings parity blockers in current flow. |
| PFarm1-zlbo.15 | Workspace P0: add missing toolchain (Add plate, Color/Fuzzy/Seam paint parity, Add text, Smart rotate, history/snap settings hooks) and align toolbar/left-tool behavior with slicer expectations. | Core operator ergonomics and day-to-day slicing throughput. |
| PFarm1-zlbo.16 | P1/P2 hardening: settings search UX, non-STL workspace parity path, cross-flow integration polish, QA and acceptance pass. | Consolidates remaining parity and reduces regression risk before close. |
| PFarm1-zlbo.5 (dependency) | Baseline contracts/data model support required by .14/.15/.16 (exact dependency scope Needs live confirm). | Prevents UI work from outrunning backend/config contracts. |

### Definition of Done (Per Bead)

#### PFarm1-zlbo.14 DoD
- [ ] Multimaterial tab is functional (not placeholder) in [src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx](../src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx).
- [ ] Process sections exist as first-class typed controls for Wall generator, Walls and surfaces, Flow ratio, Bridging, Overhangs.
- [ ] Filament area supports multi-extruder lanes (add extruder + per-lane profile context) in [src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx](../src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx).
- [ ] Process + filament selections persist and serialize correctly in submit payload.
- [ ] No regressions in existing profile edit flows (machine/filament/process).

#### PFarm1-zlbo.15 DoD
- [ ] Workspace includes parity-critical tools from inventory (Add plate, Color/Fuzzy/Seam paint, Add text, Smart rotate, history/snap controls) with visible affordances.
- [ ] Tool actions are wired or clearly marked as unavailable with explicit UX messaging (no silent no-op).
- [ ] Existing tools (move/rotate/scale/layers/support/seam paint) remain functional.
- [ ] Workspace visual behavior remains stable with WebGL-enabled canvas path.

#### PFarm1-zlbo.16 DoD
- [ ] Settings search is available and filters process settings groups/rows.
- [ ] Non-STL model flow has parity behavior plan implemented (or explicitly documented and accepted) instead of workspace bypass.
- [ ] End-to-end slice path validated across Process/Machine/Filament/Workspace with no blocker parity gaps.
- [ ] Final acceptance notes reference evidence files and close open “Needs live confirm” items.

### Suggested PFarm1-zlbo.13 Update Note (paste-ready)

Suggested command text (syntax may vary by local bd version, Needs live confirm):

`bd comment PFarm1-zlbo.13 "Added SimplyPrint parity gap inventory to devnotes/Slicer-Module-Refactor-Plan2.md with side-by-side Process/Machine/Filament/Workspace analysis, P0/P1/P2 ranking, and implementation mapping: .14 = Process+Filament P0, .15 = Workspace P0, .16 = P1/P2 hardening. Marked unresolved bead metadata/dependency specifics as Needs live confirm."`
