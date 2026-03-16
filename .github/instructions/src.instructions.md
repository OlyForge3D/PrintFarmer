---
description: "Use when working on src"
applyTo: "src/**"
---

---
description: 'PrintFarmer src area: solution-wide project layout, backend plugin system, multi-database migrations, and slicer subsystem conventions'
applyTo: 'src/**'
---

# PrintFarmer `src/` Area

The `src/` directory is the .NET solution root (`farm-web.sln`) and React frontend root. All `dotnet` commands run from here; all `npm` commands run from `src/Web/ReactApp/`. See [workspace-directories.instructions.md](.github/instructions/workspace-directories.instructions.md).

## Solution Project Map

| Directory | Assembly | Role |
|---|---|---|
| `api/` | `Farm.Web.Api` | ASP.NET Core 10 REST API + SignalR host |
| `infra/` | `Farm.Infrastructure` | Repositories, EF Core DbContext, background services, all domain services |
| `backends/Farm.Backend.Plugin.*` | Plugin assemblies | Per-backend HTTP clients (Moonraker, PrusaLink, OctoPrint, FlashForge, SDCP) |
| `backends/Farm.Backend.Plugin.Core` | Core plugin contracts | `IBackendClientPlugin`, `BackendPluginRegistry`, DI helpers |
| `discovery/` | `Farm.Shared.Discovery` | `INetworkDiscoveryProbe`, `NetworkDiscoveryService`, `BaseDiscoveryProbe` |
| `slicer/` | `Farm.Slicer.*` | Slicer plugin host, integration layer, module API |
| `signalr/Farm.SignalR` | `Farm.SignalR` | Shared SignalR hub types used by both API and workers |
| `shared/` | DTOs / shared models | Thin shared contracts (no business logic) |
| `migrations/` | Migration projects | EF Core migrations — one project per provider per context |
| `tests/` | Test projects | xUnit integration + unit tests |
| `Web/ReactApp/` | React SPA | Vite + React 19 + TypeScript frontend |

## Build Configuration (`Directory.Build.props`)

All .NET projects inherit from `src/Directory.Build.props`:
- **Target framework**: `net10.0`, `LangVersion: latest`, nullable enabled (`WarningsAsErrors: Nullable`)
- **Implicit usings**: enabled
- **Analyzers**: `AnalysisMode=All`; suppression list is centrally maintained — do not suppress warnings in individual `.csproj` files without first checking if the suppression belongs here.
- PFARM__ environment variable prefix maps to configuration: `PFARM__Spoolman__BaseUrl` → `Spoolman:BaseUrl`

## Backend Plugin System

Each printer backend is a self-contained plugin under `backends/`:

1. Implement `IBackendClientPlugin` (in `Farm.Backend.Plugin.Core`)
2. Decorate the class with `[BackendPlugin("BackendName")]`
3. Implement `INetworkDiscoveryProbe` inside the same plugin project (not in `discovery/`)
4. Register via `BackendPluginLoader` — no manual DI wiring in `api/Program.cs`

## Multi-Database Migrations

Migrations live in dedicated projects under `migrations/`, one per provider + context:

```
migrations/
  Farm.Migrations.PostgreSQL/
  Farm.Migrations.SqlServer/
  Farm.Slicer.Migrations.PostgreSQL/
  Farm.Slicer.Migrations.SqlServer/
```

Always create migrations for **both providers** when the schema changes. Run `dotnet ef migrations add` with `DB_PROVIDER=postgres` and `DB_PROVIDER=sqlserver` respectively.

## Testing Layout (`tests/`)

| Project | Coverage |
|---|---|
| `Farm.Web.Api.Tests` | API integration tests (uses `CustomWebApplicationFactory`) |
| `Farm.Web.IntegrationTests` | End-to-end cross-service tests |
| `Farm.Importing.Tests` | File import / parsing logic |
| `Farm.Slicer.Module.Tests` | Slicer module tests |
| `TestHelpers/` | Shared test utilities and builders |

Run with `dotnet test ./farm-web.sln -c Debug 2>&1 | tee /tmp/test-results.log` from `src/`. Use `TEST_USE_SHARED_SQLITE=true` to activate the in-memory SQLite fixture.

## React Frontend (`Web/ReactApp/`)

- **Key paths**: `src/services/api.ts` (central `apiClient`), `src/types/api.ts` (all API types), `src/common/hooks/useApi.ts` (shared TanStack Query hooks)
- **Feature folders**: `src/features/<feature>/{components,pages,hooks,utils}`
- **Telemetry**: OpenTelemetry browser SDK wired in `src/telemetry/`
- **Tests**: `npm run test:run` (Vitest); E2E: `npm run test:e2e` (Playwright)
- See [printfarmer-react-components.instructions.md](.github/instructions/printfarmer-react-components.instructions.md) for component conventions.
