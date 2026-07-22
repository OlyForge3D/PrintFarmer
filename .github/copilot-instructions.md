## PrintFarmer Agent Instructions

PrintFarmer is a two-tier 3D printer farm management system:

- Backend: C#/.NET API and services in `src/`.
- Frontend: React TypeScript app in `src/Web/ReactApp/`.
- Mobile: SwiftUI iOS app in `mobile/` (Xcode project, shares the API with the React frontend).
- Database: EF Core with SQLite for local development and PostgreSQL/SQL Server support for deployments.
- Real-time updates: SignalR hubs for printer and slicer events.
- Slicing: OrcaSlicer and PrusaSlicer worker services plus slicer-host APIs.

Use these instructions for durable repo conventions only. Prefer the specialized skills for detailed workflows:

- API/container debugging: `.github/skills/api-debugging/SKILL.md`
- Build and test validation: `.github/skills/testing/SKILL.md`
- OrcaSlicer profile lookup issues: `.github/skills/orcaslicer-profiles/SKILL.md`
- OrcaSlicer upgrades: `.github/skills/orcaslicer-upgrade/SKILL.md`
- PR issue-linkage: `.squad/skills/pr-issue-linkage/SKILL.md`

## Working Directories

Always run commands from the directory expected by the tool:

| Work | Directory |
|---|---|
| Git commands | repo root |
| .NET restore/build/test/format | `src/` |
| React npm commands | `src/Web/ReactApp/` |
| Xcode / swift / fastlane (iOS app) | `mobile/` |
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

## Mobile App

The SwiftUI iOS app lives in `mobile/` and was merged in from `OlyForge3D/PFarm-Ios`. It targets iOS 17+ and requires Xcode 26+ (Swift 5.9+). Architecture is MVVM + repository pattern.

API integration:

- The app supports multiple registered servers. `PRINTFARMER_API_URL` seeds/overrides
  the initial development server; for local PrintFarmer dev, use
  `http://localhost:5245` so it matches the .NET API. Legacy `pf_server_url`
  installs migrate into the registry on first launch.
- The mobile app consumes the same `/api/*` JSON contract as the React frontend — camelCase property names, string enums (see Serialization Rules below). Do not introduce mobile-only DTOs unless absolutely required; extend the shared API instead.

Common commands (run from `mobile/`):

```bash
xcodebuild -scheme PrintFarmer -destination 'platform=iOS Simulator,name=iPhone 15' build
xcodebuild test -scheme PrintFarmer -destination 'platform=iOS Simulator,name=iPhone 15'
fastlane beta   # release pipeline
```

Test suites: `PrintFarmerTests` (unit) and `PrintFarmerUITests` (UI). The app has its own `mobile/squad.config.ts` and `mobile/AGENTS.md` for agent guidance, and shares the consolidated release pipeline with the main app. See `mobile/README.md` for full setup details.

## Architecture Invariants

- The React app talks to the API at port 5245 in local development.
- The iOS app in `mobile/` consumes the same `/api/*` contract as the React frontend; both must remain compatible with the backend's camelCase + string-enum serialization.
- In microservices deployments, slicer routes are handled by slicer-host on port 5246 and routed by nginx.
- Docker is for deployment, not normal local development.
- Backend plugins contain backend-specific clients, validators, and discovery probes.
- The discovery framework interfaces live under `src/discovery/`; concrete probes live in backend plugin projects.

Route ownership in microservices mode:

| Service | Routes |
|---|---|
| Main API | Most `/api/*` endpoints |
| Slicer host | `/api/workers`, `/api/slicers`, `/api/slicer`, `/api/slice`, `/api/3d-models`, `/api/artifacts`, `/api/admin/slicer`, `/hubs/slicer` |

## Pre-PR Review Gate

**All code MUST pass 3-way adversarial review before any PR is opened.** Bishop, Hicks, and Vasquez review the branch together, debate thoroughly, and deliver a single consensus verdict. Do not open a PR until they APPROVE.

Flow:

1. Commit code to a feature branch (do not push yet).
2. Request review from Bishop, Hicks, Vasquez (mention all three).
3. Reviewers converge adversarially on the branch — no serial review or independence.
4. If consensus is APPROVE, proceed to step 5. If REJECT or BLOCK, fix the code on the branch and re-request.
5. Once APPROVED, open the PR via `gh pr create`.

This is a hard gate enforced by team policy. The trio's consensus verdict gates the PR creation step itself.

## Pull Request Issue Linkage

When opening a PR, the body **MUST** include `Closes #N` (or `Fixes #N` / `Resolves #N`) for every GitHub issue the PR resolves. GitHub will auto-close the issue when the PR merges.

**What works (GitHub auto-closes on merge):**

```
Closes #350
Closes #351
```

**What does NOT work (no auto-close):**

- Parenthetical in title: `feat(x): thing (#350)` — GitHub ignores this.
- Bead-style syntax: `[closes PFarm1-350]` — legacy, GitHub does not recognize.
- `relates to #350` — informational only, no auto-close.
- Issue number only in commit message, not in PR body.

**Verification:** After creating a PR, run `gh pr view <number> --json closingIssuesReferences` to confirm the issues are detected. If empty, update the PR body.

See `.squad/skills/pr-issue-linkage/SKILL.md` for full details and recovery procedures.

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
- Every PR is gated in CI by `dotnet ef migrations has-pending-model-changes` for all four main app and slicer context/provider migration projects.
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