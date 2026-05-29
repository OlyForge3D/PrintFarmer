## PrintFarmer Agent Instructions

PrintFarmer is a two-tier 3D printer farm management system:

- Backend: C#/.NET API and services in `src/`.
- Frontend: React TypeScript app in `src/Web/ReactApp/`.
- Database: EF Core with SQLite for local development and PostgreSQL/SQL Server support for deployments.
- Real-time updates: SignalR hubs for printer and slicer events.
- Slicing: OrcaSlicer and PrusaSlicer worker services plus slicer-host APIs.

Use these instructions for durable repo conventions only. Prefer the specialized skills for detailed workflows:

- API/container debugging: `.github/skills/api-debugging/SKILL.md`
- Build and test validation: `.github/skills/testing/SKILL.md`
- OrcaSlicer profile lookup issues: `.github/skills/orcaslicer-profiles/SKILL.md`
- OrcaSlicer upgrades: `.github/skills/orcaslicer-upgrade/SKILL.md`

## Working Directories

Always run commands from the directory expected by the tool:

| Work | Directory |
|---|---|
| Git commands | repo root |
| .NET restore/build/test/format | `src/` |
| React npm commands | `src/Web/ReactApp/` |
| Docker deploy scripts and compose | repo root |

Do not rely on the terminal's current directory. `cd` explicitly before running commands.

## Validation Commands

Use the smallest validation that covers the change. For broad or cross-layer changes, run both backend and frontend checks.

Backend:

```bash
cd src
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
dotnet test ./farm-web.sln -c Debug 2>&1 | tee /tmp/printfarmer-dotnet-test.log
dotnet format ./farm-web.sln --verify-no-changes
```

Frontend:

```bash
cd src/Web/ReactApp
npm install
npm run build
npm run test:run 2>&1 | tee /tmp/printfarmer-react-test.log
npm run lint
```

Rules:

- Do not cancel long-running restore/build/test/format commands; use generous timeouts.
- Capture long test output with `tee` and inspect the log instead of rerunning tests just to see failures.
- New warnings introduced by the current change are blockers. Existing warnings in untouched code are not a reason to widen the task unless the user asks for warning cleanup.
- If a build, test, or deployment fails, fix the failing step and rerun only the relevant failed validation.

## Local Development

Run local development natively, not in Docker:

- API: `cd src && dotnet run --project ./api/Farm.Web.Api.csproj`
- React: `cd src/Web/ReactApp && npm run dev`
- API URL: `http://localhost:5245`
- React URL: `http://localhost:3000`
- Health checks: `http://localhost:5245/healthz` and `http://localhost:5245/health`

Keep API servers and test commands in separate terminals or background processes. Verify the API is running before endpoint testing.

## Architecture Invariants

- The React app talks to the API at port 5245 in local development.
- In microservices deployments, slicer routes are handled by slicer-host on port 5246 and routed by nginx.
- Docker is for deployment, not normal local development.
- Backend plugins contain backend-specific clients, validators, and discovery probes.
- The discovery framework interfaces live under `src/discovery/`; concrete probes live in backend plugin projects.

Route ownership in microservices mode:

| Service | Routes |
|---|---|
| Main API | Most `/api/*` endpoints |
| Slicer host | `/api/workers`, `/api/slicers`, `/api/slicer`, `/api/slice`, `/api/3d-models`, `/api/artifacts`, `/api/admin/slicer`, `/hubs/slicer` |

## Serialization Rules

- All API and SignalR JSON payloads must use camelCase property names.
- Configure SignalR JSON serialization the same way as controllers.
- Frontend TypeScript interfaces must match backend JSON casing.
- Backend enums are serialized as strings through `JsonStringEnumConverter`.
- Do not parse enum API values as integers in the frontend; use string enum names such as `Brass` or `HardenedSteel`.

SignalR rules:

- SignalR event names are lowercase, such as `printerupdated` and `discoveryprogress`.
- Do not add duplicate PascalCase listeners or senders.
- Payloads such as printer status updates, discovery progress, and slicer job updates must preserve camelCase JSON.

## Data And Migrations

- Create EF Core migrations for schema changes that affect deployment databases.
- Create migrations for every affected context/provider pair.
- Main app schema changes use `Farm.Migrations.PostgreSQL` and `Farm.Migrations.SqlServer` with `AppDbContext`.
- Slicer schema changes use `Farm.Slicer.Migrations.PostgreSQL` and `Farm.Slicer.Migrations.SqlServer` with `SlicerDbContext`.
- Use descriptive PascalCase migration names.
- SQLite local development may use `EnsureCreated`; production deployments use migrations.
- Verify generated migration files exist under the affected `src/migrations/*/Migrations/` project directories.

Common main app migration commands, from `src/`:

```bash
DB_PROVIDER=postgres dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Migrations.PostgreSQL \
  --context AppDbContext

DB_PROVIDER=sqlserver dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Migrations.SqlServer \
  --startup-project ./migrations/Farm.Migrations.SqlServer \
  --context AppDbContext
```

Common slicer migration commands, from `src/`:

```bash
DB_PROVIDER=postgres dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
  --context SlicerDbContext

DB_PROVIDER=sqlserver dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Slicer.Migrations.SqlServer \
  --startup-project ./migrations/Farm.Slicer.Migrations.SqlServer \
  --context SlicerDbContext
```

## Slicer Profiles

Keep these data sources distinct:

- New slice job profile selection reads worker profiles from OrcaSlicer resources.
- Slicer Profiles management pages operate on user-owned database profiles.
- Do not merge worker-library browsing with user-owned profile management unless the user explicitly asks for that architecture change.

OrcaSlicer profile loading relies on:

- `machine_model_list`, `machine_list`, `process_list`, and `filament_list` bundles.
- `compatible_printers_condition` expressions evaluated against loaded machine profiles.
- resolved `compatible_printers` arrays for matching filament and process profiles to machine variants.

Use the OrcaSlicer profile skill for profile hierarchy, alias, and empty-profile debugging.

## Documentation And Markdown

- Update existing documentation when code changes alter user-visible behavior, setup, deployment, or architecture.
- Do not create one-off implementation markdown files unless the user asks or the content truly does not fit existing docs.
- Keep markdown concise and structured with H2/H3 headings, fenced code blocks with language identifiers, and descriptive links.

## Docker And Deployment

- Deployment scripts must run from the repo root.
- Docker file changes should respect the template hierarchy documented in `.github/instructions/docker-file-hierarchy.instructions.md`.
- When changing deployment scripts or compose templates, run the deployment script test suite described in `docs/DEPLOYMENT_TESTING_CHECKLIST.md`.
- The deployment tooling depends on Python `ruamel.yaml` for compose generation.

## Code Style

- C#: PascalCase for types/members, camelCase for locals/parameters, conventional ASP.NET Core and EF Core patterns.
- TypeScript: camelCase for variables/functions, PascalCase for components/types.
- Prefer existing local helpers, services, DTOs, query conventions, and component patterns over new abstractions.
- Keep changes focused on the user request; do not refactor unrelated code.

## Security And Secrets

- Never print or commit credentials, API keys, JWT signing keys, printer passwords, or deployment secrets.
- Read generated deployment credentials from local config only when needed for debugging, and avoid echoing secret values in final responses.
- Keep printer credentials and tokens out of tracked configuration.