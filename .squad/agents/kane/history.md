# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- **UI validation test suite** lives at `tests/ui-validation/` — standalone Playwright project that spins up real API + React servers with fresh SQLite DB
- The catalog API (`/api/catalog/manufacturers`) has a pre-existing DI bug: `CatalogCache` tries to resolve scoped `IDbContextFactory<AppDbContext>` from root provider, causing 500 errors
- The `/health` endpoint returns 503 (Unhealthy) when catalog health check fails — tests must accept 200 or 503
- On first run with empty DB, the React app shows "Initializing system..." loading screen before any interactive elements appear — tests cannot rely on buttons/links being immediately visible
- `dotnet run --project ./api/Farm.Web.Api.csproj` includes a build step that can take 60-90 seconds — global setup needs 180s timeout
- DB_PROVIDER=sqlite with ConnectionStrings__Default=`Data Source=/path/to/db` controls the SQLite database path
- NetworkDiscovery__EnableDiscovery=false prevents hitting the real network during tests
- The React dev server (vite) proxies `/api/*` and `/hubs/*` to localhost:5245, so browser tests can hit `localhost:3000/api/*`
- Existing Playwright e2e tests are in `src/Web/ReactApp/e2e/` — separate from the new UI validation suite
- Default data seeding creates 29 manufacturers (not 8 as previously documented)
- **Manufacturer entity** has a shadow property `NameLowered` with UNIQUE index — cannot insert multiple manufacturers with the same name in tests
- **Printer entity** has a UNIQUE constraint on `ServerUrl` — test printers must use distinct URLs (e.g., `http://192.168.1.{n}`)
- **Location entity** has a UNIQUE constraint on `(ParentId, Name)` — duplicate child names under the same parent are rejected at the DB level
- **Creating Printers in unit tests** requires valid FK references: seed `Manufacturer` and `PrinterModel` entities first, then reference their IDs
- **EF Core SaveChanges overrides** populate the `NameLowered` shadow property on Manufacturer from `Name.ToLowerInvariant()`
- Unit tests that create `AppDbContext` directly (not via `CustomWebApplicationFactory`) need to call `TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled()` for FK enforcement
- **FolderNode** entity has no named `DbSet` — access via `_db.Set<FolderNode>()`; uses `Path` and `FolderType` properties (not Name/Category)
