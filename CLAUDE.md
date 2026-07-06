# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

PrintFarmer is a 3D printer farm management system: ASP.NET Core 10 API backend, React 19 + TypeScript frontend, SwiftUI iOS app, EF Core multi-database persistence (SQLite/PostgreSQL/SQL Server/MySQL), and SignalR for real-time printer/slicer events.

Detailed conventions live in `.github/copilot-instructions.md`; specialized workflows in `.github/skills/*/SKILL.md` (api-debugging, testing, orcaslicer-profiles, orcaslicer-upgrade) and `.squad/skills/pr-issue-linkage/SKILL.md`.

## Working Directories

Always `cd` explicitly — do not rely on the current directory:

| Work | Directory |
|---|---|
| Git, Docker deploy scripts | repo root |
| .NET restore/build/test/format | `src/` |
| React npm commands | `src/Web/ReactApp/` |
| iOS (xcodebuild/fastlane) | `mobile/` |

## Commands

Backend (from `src/`):

```bash
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
dotnet test ./farm-web.sln -c Debug 2>&1 | tee /tmp/printfarmer-dotnet-test.log
dotnet format ./farm-web.sln --verify-no-changes

# Single test project / single test
dotnet test ./tests/Farm.Web.Api.Tests -c Debug
dotnet test ./farm-web.sln --filter "FullyQualifiedName~MyTestClass" -c Debug
```

Test projects: `src/tests/` — `Farm.Web.Api.Tests`, `Farm.Web.IntegrationTests`, `Farm.Slicer.Module.Tests`, `Farm.OrcaSlicer.Worker.Tests`.

Frontend (from `src/Web/ReactApp/`):

```bash
npm install
npm run build
npm run test:run          # vitest (all)
npm run test:run -- src/path/to/Component.test.tsx   # single file
npm run lint
npm run test:e2e          # Playwright
```

Local development runs natively, not in Docker (Docker is for deployment only):

```bash
cd src && dotnet run --project ./api/Farm.Web.Api.csproj   # API → http://localhost:5245
cd src/Web/ReactApp && npm run dev                          # UI  → http://localhost:3000
```

Health checks: `http://localhost:5245/healthz` and `/health`.

Rules:
- Don't cancel long restore/build/test commands; use generous timeouts and `tee` long output to a log instead of rerunning to see failures.
- New warnings introduced by your change are blockers; pre-existing warnings in untouched code are not your problem unless asked.

## Architecture

Two-tier client-server:

```
React app (:3000) + iOS app (mobile/)
    ↕ REST /api/* + SignalR /hubs/*
ASP.NET Core API (:5245)  [+ slicer-host (:5246) in microservices mode]
    ↕ EF Core → SQLite / PostgreSQL / SQL Server / MySQL
```

- **`src/api/`** — main API (`Farm.Web.Api`): controllers, hubs, services, DTOs, startup.
- **`src/backends/`** — one plugin project per printer backend (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge, Core, TestEmulator). Each plugin owns its backend-specific clients, validators, and discovery probes. No circular dependencies between plugins.
- **`src/discovery/`** — discovery framework *interfaces* only; concrete probes live in the backend plugins.
- **`src/slicer/`** — slicer module, host API, and integration; **`src/Slicers/`** — versioned OrcaSlicer implementations; **`src/orcaslicer-worker/`**, **`src/worker-shared/`** — slicing workers.
- **`src/migrations/`** — provider-specific EF Core migration projects (see below).
- **`src/Web/ReactApp/`** — Vite + React 19 + Tailwind v4 + TanStack Query frontend.
- **`mobile/`** — SwiftUI iOS app (MVVM + repositories), consumes the same `/api/*` contract as the React app; has its own `mobile/AGENTS.md`.

In microservices deployments, nginx routes `/api/workers`, `/api/slicers`, `/api/slicer`, `/api/slice`, `/api/3d-models`, `/api/artifacts`, `/api/admin/slicer`, and `/hubs/slicer` to slicer-host (:5246); everything else goes to the main API.

## Serialization Contract (cross-cutting)

- All API and SignalR JSON uses **camelCase** property names; SignalR JSON is configured the same way as controllers.
- Enums serialize as **strings** (`JsonStringEnumConverter`) — frontend must never parse enum values as integers.
- SignalR event names are **lowercase** (`printerupdated`, `discoveryprogress`); do not add duplicate PascalCase listeners/senders.
- The React and iOS apps share this contract — keep both compatible; avoid mobile-only DTOs.

## EF Core Migrations

Schema changes require migrations for **every affected context/provider pair** (CI gates PRs with `dotnet ef migrations has-pending-model-changes` on all four):

- Main app (`AppDbContext`): `Farm.Migrations.PostgreSQL` and `Farm.Migrations.SqlServer`
- Slicer (`SlicerDbContext`): `Farm.Slicer.Migrations.PostgreSQL` and `Farm.Slicer.Migrations.SqlServer`

From `src/` (swap provider/project/context as needed):

```bash
DB_PROVIDER=postgres dotnet ef migrations add <PascalCaseName> \
  --project ./migrations/Farm.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Migrations.PostgreSQL \
  --context AppDbContext
```

SQLite local dev may use `EnsureCreated`; production uses migrations. Verify generated files land under `src/migrations/*/Migrations/`.

## Slicer Profiles

Two distinct data sources — do not merge them:
- New slice job profile selection reads **worker profiles from OrcaSlicer resources**.
- Slicer Profiles management pages operate on **user-owned database profiles**.

Profile loading depends on `machine_model_list`/`machine_list`/`process_list`/`filament_list` bundles and `compatible_printers_condition` evaluation — use the orcaslicer-profiles skill for debugging.

## PR Workflow

- **Pre-PR review gate (hard team policy):** all code must pass 3-way adversarial review by Bishop, Hicks, and Vasquez (squad agents) before any PR is opened. Commit to a feature branch, request review from all three, and only run `gh pr create` after their consensus APPROVE.
- **Issue linkage:** the PR body must contain `Closes #N` / `Fixes #N` for every issue it resolves (title parentheticals and `relates to #N` do not auto-close). Verify with `gh pr view <number> --json closingIssuesReferences`.
- Task tracking uses GitHub issues (`gh issue create/list/view`).

## Other Conventions

- Prefer existing helpers, services, DTOs, and component patterns over new abstractions; keep changes scoped to the request.
- Update existing docs under `docs/` when behavior/setup/deployment changes; don't create one-off implementation markdown files.
- Docker file changes must respect the template hierarchy in `.github/instructions/docker-file-hierarchy.instructions.md`; deployment tooling needs Python `ruamel.yaml`.
- Never print or commit credentials, printer passwords, JWT keys, or deployment secrets.
