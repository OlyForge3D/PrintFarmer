# PrintFarmer

![CI](https://github.com/jpapiez/PrintFarmer/actions/workflows/ci.yml/badge.svg)
![Containers](https://github.com/jpapiez/PrintFarmer/actions/workflows/containers.yml/badge.svg)
![Dependency Review](https://github.com/jpapiez/PrintFarmer/actions/workflows/dependency-review.yml/badge.svg)
![Codecov](https://img.shields.io/codecov/c/github/jpapiez/PrintFarmer)
![Scorecard](https://img.shields.io/ossf-scorecard/github.com/jpapiez/PrintFarmer?label=openssf-scorecard)
![CodeQL](https://github.com/jpapiez/PrintFarmer/actions/workflows/codeql.yml/badge.svg)
<!-- SOFT_FREEZE_BADGE_START -->
![Soft Freeze](https://img.shields.io/badge/soft%20freeze-active-red)
<!-- SOFT_FREEZE_BADGE_END -->

A React TypeScript dashboard for managing multiple 3D printers.

> NOTE: The repository is currently under a soft freeze for MVP stabilization. See `SOFT_FREEZE.md` for permitted changes and exception process.

**📋 [Deployment Overview](DEPLOYMENT_OVERVIEW.md)** - Choose the right deployment approach for your needs  
**🔧 [Local Development Guide](LOCAL_DEVELOPMENT.md)** - Development setup, hot reload, debugging  
**🐳 [Docker Deployment Guide](DOCKER_DEPLOYMENT.md)** - Production containers, scaling, monitoring  
**🏗️ [Slicer Microservices Architecture](documentation/architecture/slicer-microservices.md)** - Distributed slicing system architecture and ADRs  
**📡 [Service Interfaces Documentation](INTERFACE_DOCUMENTATION_SUMMARY.md)** - Complete API service interfaces with XML documentation  
**🤝 [Contributing Guide](CONTRIBUTING.md)** - Development workflow, testing, code standards
**🧊 Soft Freeze Policy**: See `SOFT_FREEZE.md` (active if `.soft-freeze` file present)

## Features
- **Multi-backend Support**: Moonraker and PrusaLink API integration
- **Real-time Updates**: Live printer status via SignalR
- **Distributed Slicing**: Microservices architecture for scalable G-code generation
- **Network Discovery**: Automatic detection of printers on your network
- **Modern UI**: React TypeScript frontend with responsive design
- **Flexible Database**: SQLite, PostgreSQL, SQL Server, MySQL (all validated; uniform behavior confirmed across providers)
- **Docker Ready**: Production deployment with containers
- **WiFi Friendly**: Works with WiFi-connected printers (local development)
- **Signed Images**: Cosign-signed container images (keyless OIDC or key-based)
- **Provenance Attestations**: SLSA provenance tying digests to build workflow
 - **Robust Catalog Layer**: Canonical name normalization, case‑insensitive uniqueness, duplicate conflict (409) handling, weak ETag caching

## Quick Start - Choose Your Path

### 🚀 **Automated Docker Deployment (Recommended for Production)**
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
cp .env.example .env   # Review & edit environment variables (JWT key, DB provider, optional Spoolman, admin bootstrap)
chmod +x scripts/deploy-docker.sh
./scripts/deploy-docker.sh
```
The script will guide you through configuration and deploy everything automatically.

### 💻 **Local Development (Recommended for Development)**
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
cp .env.example .env   # Optional for overriding defaults
chmod +x scripts/pf-dev.sh
./scripts/pf-dev.sh bootstrap   # one-time dependency restore
./scripts/pf-dev.sh start       # starts API + React (background)
./scripts/pf-dev.sh status      # verify running
```
To stop later:
```bash
./scripts/pf-dev.sh stop
```
Direct development on your machine without containers (fastest inner loop & full WiFi device discovery).

### 📖 **Detailed Guidance**
**Not sure which approach to use?** See our comprehensive [Deployment Overview](DEPLOYMENT_OVERVIEW.md) that helps you choose based on your specific needs.

## Architecture

### Two-Tier Modern Stack
```
React TypeScript Frontend (localhost:3000)
           ↕ HTTP + SignalR
ASP.NET Core API Backend (localhost:5245)  
           ↕
      Database (SQLite/PostgreSQL/etc.)
```

### Repository Structure
```
src/
  api/              # ASP.NET Core API server (.NET 9)
  Web/ReactApp/     # React TypeScript frontend (Vite + React 19)  
  shared/           # DTOs and models shared between frontend/backend
  tests/            # Integration and unit tests
  farm-web.sln      # .NET solution file
```

### Engine Slicing Workers

PrintFarmer now uses dedicated engine-specific worker services for G-code generation instead of a generic combined slicer service:

| Worker | Purpose | Dockerfile | Default Queue |
|--------|---------|------------|---------------|
| OrcaSlicer Worker | Slicing via OrcaSlicer engine | `Dockerfile.orcaslicer` | `orcaslicer-jobs` |
| PrusaSlicer Worker | Slicing via PrusaSlicer engine | `Dockerfile.prusaslicer` | `prusaslicer-jobs` |

All engine workers now share a unified core library (`worker-shared` / `Farm.Slicer.Worker.Core`) that provides:
- Base Redis queue consumer with processing list tracking
- Shared progress reporting over HTTP
- Centralized worker state (active jobs, shutdown flag, capacity)
- Graceful shutdown background service

Engine-specific projects only implement their slicing pipeline (`*SlicingPipelineService`) and binary detection logic. Former duplicated per-engine implementations (`HttpProgressReporter`, `WorkerStateService`, `GracefulShutdownService`, interface definitions) have been removed in favor of the shared core abstractions. Temporary placeholder stubs used during the migration have now been cleared (empty files replaced/removed) so only the shared `Farm.Slicer.Worker.Core` definitions remain.

Base runtime image layering:
1. `Dockerfile.slicer-base` – Neutral hardened runtime (GTK/offscreen deps, non-root user, health infra)
2. Engine Dockerfile – Adds engine binary + worker application (entrypoint provided by project)

Removed legacy components:
- Generic `slicer-worker` project (superseded by per-engine workers)
- Historical `Dockerfile.base` (replaced by `Dockerfile.slicer-base`)
- Duplicated per-worker infrastructure classes (now centralized in `Farm.Slicer.Worker.Core`)

To add a new engine worker:
1. Create a new project `Farm.<EngineName>Slicer.Worker` modeled after an existing worker.
2. Reference the shared core project (`worker-shared/Farm.Slicer.Worker.Core.csproj`).
3. Implement `ISlicingPipelineService` for the engine-specific slicing logic.
4. Implement any engine-specific binary detection (`I<Engine>BinaryDetector`).
5. Add a Dockerfile similar to `Dockerfile.orcaslicer` layering on `Dockerfile.slicer-base`.
6. Register it in `docker-compose.microservices.yml` and configure `SlicerOrchestrator__Workers__<EngineName>` env var in the API service.
7. Define a distinct Redis queue name (e.g., `<engine>-jobs`).

Shared environment variables (ports, Redis, storage endpoint, queue naming) are documented here:
**➡️ [Shared Worker Environment Variables](docs/slicer/worker-environment.md)**

Port mapping: each worker listens internally on `8080`; external host ports (e.g. `8081` Orca, `8082` Prusa) are defined by compose/Kubernetes configuration, not by changing `ASPNETCORE_URLS` inside the image.

Health & readiness endpoints: each worker exposes `/healthz` (liveness) and readiness via the same endpoint (engine initialization performs binary detection early and fails fast if missing).

Graceful shutdown: workers finish active jobs then exit (shutdown timeout managed by host/container orchestrator; future enhancement could add configurable timeout via `WORKER_SHUTDOWN_TIMEOUT`).


## Detailed Documentation

**📋 [Deployment Overview](DEPLOYMENT_OVERVIEW.md)** - Choose the right deployment approach for your needs  
**🔧 [Local Development Guide](LOCAL_DEVELOPMENT.md)** - Development setup, hot reload, debugging  
**🐳 [Docker Deployment Guide](DOCKER_DEPLOYMENT.md)** - Production containers, scaling, monitoring  
**🤝 [Contributing Guide](CONTRIBUTING.md)** - Development workflow, testing, code standards

## System Requirements

### Local Development
- **.NET SDK 9.0+** (exactly 9.0.302 as specified in global.json)
- **Node.js 18+** and npm (for React frontend)
- **macOS/Windows/Linux** (macOS recommended for WiFi device access)

**Additional macOS Requirements:**
- **Homebrew** - Package manager for installing development tools
- **GNU Coreutils** - Provides `timeout` command for build scripts
- *These will be automatically installed by the local setup script*

### Docker Deployment  
- **Docker 20.10+** and Docker Compose v2
- **Linux** (recommended for full networking), **Windows**, or **macOS**
- **4GB+ RAM** and **10GB+ storage** for containerized deployment

## Key API Endpoints

- `GET /healthz` (alias: `/api/healthz`) — Basic health check
- `GET /health` (alias: `/api/health`) — Comprehensive health status
  - Health JSON reflects provider + background service status; SignalR fan-out indirectly validated via connection stats endpoint used in container health checks.
- `GET /api/printers` — List all configured printers  
- `POST /api/printers` — Add a new printer
- `POST /api/printers/discover-streaming` — Real-time network discovery
- `GET /api/network-discovery/settings` — Network discovery configuration
- **SignalR Hub**: `/hubs/printers` — Real-time printer status updates

### Environment & Secrets (.env)
An `.env.example` file is provided. Copy it to `.env` and customize before production deployment. Key items:

| Variable | Purpose | Notes |
|----------|---------|-------|
| `Jwt__Key` | JWT signing secret | Use a 64+ char random string in production |
| `DB_PROVIDER` | Database provider | Sqlite (default), Postgres, SqlServer, MySql |
| `ENABLE_ADMIN_BOOTSTRAP` | Opt-in first admin creation | Must be `true` AND ADMIN_* vars set; disable after first run |
| `SPOOLMAN_ENABLED` / `SPOOLMAN_BASE_URL` | Filament inventory integration | Only seeds if `SPOOLMAN_ENABLED=yes` |
| `SlicerOrchestrator__EnableDistributedSlicing` | Distributed slicer orchestration | true enables worker endpoints |

Initial admin creation now uses either:

1. React Setup Wizard (appears automatically if `needsSetup=true`).
2. Admin CLI tool (headless environments).

Env‑based automatic bootstrap has been removed for security. If you still prefer an environment driven one‑shot bootstrap during container initialization you can temporarily set:
```
ENABLE_ADMIN_BOOTSTRAP=true
ADMIN_USERNAME=admin
ADMIN_EMAIL=admin@example.com
ADMIN_PASSWORD=ChangeMeSuperStrong123!
```
The application logs show success; remove these values immediately afterwards. Recommended approach is the CLI instead.

#### Admin CLI (Headless / Automation)

Create the first admin when no browser is available:
```
dotnet run --project src/tools/AdminCli -- --status
dotnet run --project src/tools/AdminCli -- \
  --username admin \
  --email admin@example.com \
  --password "ChangeMeSuperStrong123!" \
  --first-name Admin \
  --last-name User
```
Output includes a JWT token if creation/login succeeds. The command is idempotent: if the same credentials already created the admin it returns a fresh token.

Additional users can be added via authenticated `POST /api/users` (requires `farm_admin`).

#### /api/users Schemas

`POST /api/users` (admin only) request body (password must be >=12 chars for admin accounts—enforced by server):
```
{
  "username": "jdoe",
  "email": "jdoe@example.com",
  "password": "StrongPassw0rd!",
  "firstName": "Jane",
  "lastName": "Doe",
  "roleIds": ["<role-guid>"]
}
```
Response (201): Full `UserDto` object:
```
{
  "id": "<guid>",
  "username": "jdoe",
  "email": "jdoe@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "isActive": true,
  "emailConfirmed": false,
  "lastLogin": null,
  "createdAt": "2025-09-11T12:34:56Z",
  "roles": ["farm_user"],
  "permissions": []
}
```

`GET /api/users` returns an array of `UserDto` objects.

`PUT /api/users/{id}` update body (partial):
```
{
  "firstName": "Jane",
  "lastName": "Operator",
  "isActive": true,
  "roleIds": ["<updated-role-guid>"]
}
```
`DELETE /api/users/{id}` removes the user (cannot self‑delete: returns 400).

Role discovery: `GET /api/users/roles` returns `RoleDto[]` (each with `rolePermissions`).

Authentication Tokens: For initial setup, use the Setup Wizard or the headless CLI. After login, store the `Authorization: Bearer <token>` header for subsequent user management calls.

### G-code Harvesting

Core endpoints:
- `POST /api/gcode-harvest/start` – Start a harvest for a printer (body: StartGcodeHarvestDto)
- `GET /api/gcode-harvest/{operationId}` – Get operation details
- `GET /api/gcode-harvest/active/{printerId}` – Active operation for a printer
- `GET /api/gcode-harvest/recent/{printerId}?count=10` – Recent operations
- `GET /api/gcode-harvest/active` – All active operations

Start payload (selected fields):
```
{
  printerId: Guid,
  includeSubdirectories: bool,            // default: true
  maxFileSizeBytes: long?                 // upper size filter
  minFileSizeBytes: long?                 // lower size filter
  modifiedAfter: string? (ISO 8601),      // only files modified after this timestamp
  fileExtensions: string[]?               // extension list without dot, e.g. ["gcode","gco"]
  duplicateHandling: "skip"|"overwrite"|"rename" // default: skip
}
```

Duplicate handling semantics:
- `skip`: Do not import if a matching file (same hash or name heuristic) already exists; increments FilesSkipped.
- `overwrite`: Replace existing library entry metadata & content; increments FilesAdded.
- `rename`: Import as a new file; auto-appends `-copy`, `-copy2`, etc. to avoid collisions; increments FilesAdded.

Filtering order during discovery:
1. Extension allowlist (if provided)
2. Size range (min / max)
3. Modified-after cutoff

Progress fields on `GcodeHarvestOperation`:
`filesFound`, `filesAdded`, `filesSkipped`, `filesErrored`, `totalBytesProcessed`, plus status (`Running|Completed|Failed|Cancelled`).

Real-time updates are delivered over the existing `/hubs/printers` SignalR connection (harvest operations broadcast alongside printer status events).

Development note: Schema currently uses `EnsureCreated()` (no active EF migrations). A temporary migration was generated and removed (2025-09-06) to retain rapid iteration.

## Network Discovery Features

**Automatic Printer Detection:**
- Scans configurable IP ranges for Moonraker/Klipper printers
- Real-time progress updates via SignalR
- Supports WiFi and Ethernet connected devices

**Platform Considerations:**
- **Local Development**: Full WiFi device access on all platforms
- **Docker on Linux**: Full network access with proper configuration  
- **Docker on macOS**: Limited WiFi device access (use local development)
- **Docker on Windows**: Good network access with Windows containers

## Technology Stack

### Frontend (React TypeScript)
- **React 19** with TypeScript for type safety
- **Vite** for fast development and optimized builds
- **Tailwind CSS** for modern responsive design
- **React Query** for server state management
- **SignalR Client** for real-time updates

### Backend (ASP.NET Core)
- **.NET 9** with ASP.NET Core API
- **Entity Framework Core** with multi-database support  
- **SignalR** for real-time communication
- **Refit** for external API clients (Moonraker, PrusaLink)
- **FluentValidation** for input validation

### Database Support
- **SQLite** (default) - Simple file-based database
- **PostgreSQL** (recommended for production) - Advanced features
- **SQL Server** - Enterprise database support
- **MySQL** - Popular open-source option

#### Validation Status (Sept 2025)
All four providers have been integration-tested for the catalog subsystem (manufacturer & model CRUD, normalization, duplicate detection, weak ETag conditional GET). Behavior is identical across:

| Provider | Status | Notes |
|----------|--------|-------|
| SQLite   | ✅ | Baseline local & tests |
| PostgreSQL | ✅ | Passed full catalog integration tests |
| MySQL    | ✅ | Passed full catalog integration tests |
| SQL Server | ✅ | Tests green (container health probe occasionally reports unhealthy due to emulation timing, but connections succeed) |

Schema creation currently uses `EnsureCreated()` (no migrations yet) to keep iteration speed high during soft-freeze; shadow lowercase columns (`NameLowered`) and unique indexes are created for each provider automatically. Migrations will be introduced post-freeze.

#### Selecting a Provider
Set `DB_PROVIDER` and the matching connection string environment variable (examples):
```
DB_PROVIDER=Sqlite
ConnectionStrings__Default=Data Source=farm.db

DB_PROVIDER=Postgres
ConnectionStrings__Postgres=Host=postgres;Database=printfarmer;Username=...;Password=...

DB_PROVIDER=SqlServer
ConnectionStrings__SqlServer=Server=sqlserver,1433;Database=printfarmer;User Id=sa;Password=Your_password123;TrustServerCertificate=True

DB_PROVIDER=MySql
ConnectionStrings__MySql=Server=mysql;Port=3306;Database=printfarmer;User=...;Password=...;TreatTinyAsBoolean=false
```
If an unsupported value is supplied, the application falls back to SQLite.

### Catalog Normalization & Duplicate Handling
The catalog layer (Manufacturers & Printer Models) applies a canonical name normalization pass on create/update. The normalized (canonical) value is returned via the `X-Normalized-Name` response header for idempotent client reconciliation. Duplicate submissions (including case-only differences) trigger a `409 Conflict` with a RFC 7807 ProblemDetails payload and the header still emitted to aid automatic client-side correction.

Key behaviors:
* Case-insensitive uniqueness is enforced by shadow `NameLowered` columns + database unique indexes AND pre-save in-memory checks for clearer 409 responses.
* Normalization trims, collapses interior excessive whitespace, and standardizes casing rules (implementation in `CatalogNameNormalizer`).
* Weak ETags (format `W/"<hash>"`) are emitted for list endpoints (`/api/catalog/manufacturers`, `/api/catalog/models?manufacturerId=...`). Clients using `If-None-Match` receive `304 Not Modified` when content hasn’t changed.
* GET-by-id endpoints are available for both manufacturers and models.

Why this matters:
* Prevents “ghost” duplicates differing only by case/spacing across heterogeneous databases.
* Provides deterministic client reconciliation (clients can update their local display name using the header).
* Reduces bandwidth & improves perceived latency via conditional GET + in-memory cache.

Client Guidance:
1. Always read `X-Normalized-Name` after a create/update and update local state if it differs from the submitted value.
2. On 409, surface the ProblemDetails message and optionally retry with the canonical value if appropriate.
3. Use `If-None-Match` with the ETag from the previous list response to avoid unnecessary refresh traffic.

Future Enhancements (planned):
* Introduce migrations to persist normalization metadata changes safely.
* Stronger hash diversification (e.g., include count + incremental version) for catalog ETags if/when partial-list filtering is added.
* Optional locale-aware normalization customization via configuration.

## Development Workflow

### Tooling
Additional developer & operational tools:
* Admin CLI (`src/tools/AdminCli`) – headless initial admin creation and status check.
  * Examples:
    * `dotnet run --project src/tools/AdminCli -- --status`
    * `dotnet run --project src/tools/AdminCli -- --username admin --email admin@example.com --password "ChangeMeSuperStrong123!" --first-name Admin --last-name User`
* Quiet Test Runner (see `scripts/run-tests-quiet.sh`) – generates concise TRX summaries.
* Network/SignalR health script `signalr-health-check.sh` – probes hub liveness.

Planned additions: password policy editing UI (backed by new `/api/settings/security/password-policy` endpoints).

### Local Development (Recommended)
Preferred (script):
```bash
./scripts/pf-dev.sh start
```

### Test Timing Instrumentation

PrintFarmer includes an opt-in lightweight per-test timing instrumentation system for xUnit integration tests. It provides:

- Per-test CSV logging (timestamp, duration, category, class, method)
- Optional percentile & hotspot summary at the end of a run
- Manual categorization via an attribute OR automatic instrumentation for all tests not already attributed

#### Environment Variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `PF_TIMING` | unset (off) | Master switch. Set to `1`/`true` to enable writing `test-timings.csv`. `0` disables. |
| `PF_TIMING_AUTO` | unset (off) | When enabled (`1`/`true`) automatically times every test method that does not already have the `TestTimingAttribute`. Logged with category `Auto`. |
| `PF_TIMING_RESET` | unset | If set to `1`/`true` a new `RUN,<id>,<UTC start>` marker is appended before first write, allowing multiple logical runs to share one CSV. |
| `PF_TIMING_SUMMARY` | unset (on) | Set to `0`/`false` to suppress generation of `test-timings-summary.txt` + console summary on process exit. |

#### Manual Timing & Categories

Add the attribute to a test (or test class) to control inclusion & category label:

```csharp
[TestTiming("DbHeavy")] // category appears in CSV instead of Auto
public class DbHeavyTests {
  [Fact]
  public async Task DoesWork() { /* ... */ }
}
```

If the attribute is applied at class level all test methods inherit that category. Auto instrumentation will detect the attribute (class or method) and will NOT double-log.

#### Output Files

Files are emitted to the test binary output directory (e.g. `src/tests/Farm.Web.Api.Tests/bin/Debug/net9.0/`):

- `test-timings.csv` – Raw entries and RUN markers
- `test-timings-summary.txt` – Percentiles (P50/P90/P95/P99), mean, top 10 slowest executions, heaviest classes by P90

#### Sample Usage

Full suite (opt-in auto instrumentation + summary + fresh RUN block):

```bash
PF_TIMING=1 PF_TIMING_AUTO=1 PF_TIMING_RESET=1 dotnet test src/farm-web.sln -c Debug
```

Only manually attributed tests (no auto timing):

```bash
PF_TIMING=1 dotnet test src/farm-web.sln -c Debug
```

Disable summary (raw CSV only):

```bash
PF_TIMING=1 PF_TIMING_AUTO=1 PF_TIMING_SUMMARY=0 dotnet test src/farm-web.sln -c Debug
```

#### Interpreting the CSV

Format (header omitted here):

```
2025-09-12T23:18:49.1284880Z,1245.95,JobStatus,Farm.Web.Api.Tests.SlicerServices.OrcaSlicerWorkerIntegrationTests,GetJobStatus_ForNonExistentJob_ShouldReturnNull
```

Columns: `TimestampUtc,DurationMs,Category,Class,Method`

`RUN,<runId>,<utc-start>` markers delineate logical runs when `PF_TIMING_RESET` is used between invocations.

#### Notes

- Overhead: measuring + file append is typically sub‑millisecond; safe for broad use when diagnosing performance regressions.
- Auto instrumentation intentionally ignores any test already annotated to avoid duplicate lines.
- The summary only considers entries after the last `RUN,` marker (if present) to keep multi-run CSVs manageable.
- Future enhancements (namespace → category mapping, exclusion filters) can be layered without changing existing file formats.

---
Manual alternative:
1. API Backend: `dotnet run --project api/Farm.Web.Api.csproj`  
2. React Frontend: `cd Web/ReactApp && npm run dev`
3. Open: http://localhost:3000 (auto-connects to API)

### Docker Development
1. **Automated**: `./scripts/deploy-docker.sh`
2. **Manual**: `docker compose up -d --build`
3. **Open**: http://localhost:8080

## Testing

### Automated Tests
```bash
# .NET API tests (62 integration tests)
cd src && dotnet test ./farm-web.sln

# React component tests  
cd src/Web/ReactApp && npm test
```

### Manual Verification
```bash
# Test API basic health (either original or alias path)
curl http://localhost:5245/healthz
# or
curl http://localhost:5245/api/healthz

# Test network discovery
curl -X POST http://localhost:5245/api/printers/discover-streaming

# Test React app
curl http://localhost:3000/

# Verify signed container image (requires cosign installed)
cosign verify ghcr.io/jpapiez/printfarmer-api:latest \
  --certificate-identity-regexp 'https://github.com/jpapiez/PrintFarmer' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com

# Fetch provenance artifact reference (example)
gh api repos/jpapiez/PrintFarmer/actions/runs --jq '.workflow_runs[0].id' # then download artifacts from that run
```

### Attestations

SBOM and vulnerability scan results are attached as cosign attestations:
```bash
FIRST_TAG=ghcr.io/jpapiez/printfarmer-api:latest
cosign verify-attestation $FIRST_TAG \
  --certificate-identity-regexp 'https://github.com/jpapiez/PrintFarmer' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com | jq '.'

### Supply Chain Verification Workflow
To verify a released tag (example v1.2.3):
```bash
gh workflow run verify-supply-chain.yml -f tag=v1.2.3
```
Then inspect the workflow run logs for signature and attestation verification.
```

## Configuration

### Database Configuration
Environment variables control database selection:
```bash
# SQLite (default)
DB_PROVIDER=sqlite
ConnectionStrings__Default=Data Source=farm.db

# PostgreSQL  
DB_PROVIDER=postgres
ConnectionStrings__Postgres=Host=localhost;Database=printfarmer;...
```

### Network Discovery
Configure IP ranges to scan for printers:
```bash
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
```

### Development vs Production
```bash
# Development
ASPNETCORE_ENVIRONMENT=Development  # Enables Swagger, detailed logging

# Production  
ASPNETCORE_ENVIRONMENT=Production   # Optimized for performance
```

### Environment Variables Reference

Core application behavior is driven via environment variables (used in Docker Compose, Kubernetes manifests, or local shells). Below is a non‑exhaustive but curated list of the most relevant variables now supported:

Authentication & Security:
- Jwt__Key (required) – 32+ char symmetric signing key for JWT tokens.
- ADMIN_USERNAME / ADMIN_EMAIL / ADMIN_PASSWORD – Optional unattended bootstrap of the first admin user (created only if no existing farm_admin). Password must be 8+ chars.

Spoolman Integration:
- SPOOLMAN_ENABLED=yes|no – When 'yes' and SPOOLMAN_BASE_URL is set, seeds config at startup if not already present.
- SPOOLMAN_BASE_URL=http://spoolman:7912 – Base URL to the Spoolman instance.
- SPOOLMAN_PORT=7912 (informational; discovery/diagnostics only – actual URL supersedes).

Network Discovery:
- DISCOVERY_PORTS=7125,80 – Comma/space/semicolon separated list of TCP ports to probe (Moonraker + generic HTTP by default).
- DISCOVERY_RANGES=192.168.1.0/24,10.0.0.0/24 – Optional seed of network ranges; can be reapplied via POST /api/network-discovery/settings/apply-env.
- ALLOW_LOCAL_NETWORK=true – If true, CORS opens to any origin (dev convenience). Otherwise origin filtering applies.
- ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12 – CIDR ranges allowed for CORS dynamic origin evaluation when ALLOW_LOCAL_NETWORK=false.

Database & Storage:
- DB_PROVIDER=Sqlite|Postgres|SqlServer|MySql – Selects EF Core provider (Sqlite default).
- ConnectionStrings__Default=Data Source=/data/farm.db – Sqlite path (mounted volume in container).

Redis & Distributed Slicing:
- Redis__ConnectionString=redis:6379 – API/queue connection (service DNS inside Docker network).
- ConnectionStrings__Redis=redis:6379 – Worker connection string.
- SlicerOrchestrator__EnableDistributedSlicing=true|false – Toggle external slicer worker orchestration.

Deployment Mode & SPA:
- DEPLOYMENT_MODE=monolithic|microservices – Monolithic will serve static React assets (or dev proxy in development).

Diagnostics & Startup Tuning:
- DB_CONNECTION_RETRY_COUNT / DB_CONNECTION_RETRY_DELAY – Control DB initialization resilience (defaults 3 / 2s).

Deprecated / Removed:
- DOCKER_HOST_NETWORK (previously used for host networking hints) – No longer required after explicit port mapping refactor.

Auto-Detect & Settings Endpoints:
- /api/network-discovery/auto-detect (admin) – Enumerates local interfaces to suggest CIDR ranges.
- /api/network-discovery/settings – Persisted discovery configuration (ranges, timeoutMs, maxConcurrentScans, ports).

Spoolman Management Endpoints:
- POST /api/spoolman/config – Set (admin-protected).
- DELETE /api/spoolman/config – Clear.
- GET /api/spoolman/health – Lightweight connectivity probe.

Admin Bootstrap Notes:
If ADMIN_* vars are supplied and no admin exists, a bootstrap user is created (FirstName: Admin, LastName: Bootstrap). Future runs skip creation once any active farm_admin is present. For security in production, consider unsetting these after first start.

### G-code Virtual File Browser (Filesystem-backed)

The `/api/gcode-files` endpoints expose a non-recursive, filesystem-backed view of a configurable G-code library root used primarily by the React file browser. Key behaviors and related environment variables:

Environment Variables:
- `GCODE_LIBRARY_ROOT` – Optional absolute path overriding the default internal root. A `gcode-library/` subdirectory is created under this path. Safe path resolution prevents escaping this root.
- `GCODE_WEAK_ETAGS=1` – When set (any non-empty value), download (and HEAD) responses emit weak ETags (`W/"<token>"`) instead of strong. Use this if future server-side post-processing (e.g., metadata injection) might leave on-disk bytes unchanged while representation semantics evolve.

Endpoints (selected):
- `GET /api/gcode-files` – List entries in a virtual directory (immediate children only). Supports: `page`, `pageSize` (clamped to 500), `search` (substring match), `sort` (`name|size|date`), `direction` (`asc|desc`). Returns: `files[], totalFiles, totalSize, page, pageSize, totalPages, totalItems`.
- `DELETE /api/gcode-files` (JSON body `{ paths: string[] }`) – Deletes one or more `.gcode` files.
- `GET /api/gcode-files/download?path=/relative.gcode` – Streams a file and emits `ETag` + `Last-Modified`; honors conditional caching (`If-None-Match`, `If-Modified-Since`) and supports `HEAD`.

Delete Semantics & Response Contract:
The delete endpoint now returns granular outcome telemetry even on partial success. Mixed file + directory requests no longer hard-fail the entire batch.

Response shape:
```
{
  requested: string[],        // All normalized requested virtual paths
  deletedFiles: string[],     // Paths actually deleted this call
  skipped: string[],          // Paths that did not exist (benign)
  failed: string[],           // Paths that could not be deleted (e.g. directories, IO issues)
  totalRequested: number,
  totalSucceeded: number,     // == deletedFiles.length
  totalSkipped: number,       // == skipped.length
  totalFailed: number         // == failed.length
}
```

Status codes:
- `200 OK` – At least one file path was valid (even if some paths failed or were skipped). Any directories included in a mixed set appear under `failed`.
- `400 Bad Request` – All provided paths resolve to directories (directory deletion is intentionally not supported) OR the client supplied an empty/invalid payload.

ETag Behavior:
- Strong ETag (default): Derived from file last-modified ticks + size; format: `"<hex-timestamp>-<size>"`.
- Weak ETag (when `GCODE_WEAK_ETAGS` set): Prefixed with `W/` and otherwise same token generation; safe for clients that prefer tolerant cache revalidation when semantic representation may diverge.

Conditional Requests:
- `If-None-Match` has precedence over `If-Modified-Since` if both supplied.
- A 1-second tolerance is applied to `If-Modified-Since` comparisons to accommodate filesystem timestamp precision differences across platforms.

HEAD Requests:
`HEAD /api/gcode-files/download?path=...` returns the same headers (`Content-Length`, `ETag`, `Last-Modified`) as `GET` without a body, enabling lightweight existence & cache probes.

Security Notes:
- Path normalization rejects attempts to traverse above the configured root (e.g. `..` segments after canonicalization).
- Only `.gcode` files are currently listed/deletable; other extensions are ignored and treated as non-existent.

Future Enhancements (tracked separately):
- Optional recursive listing / lazy directory tree expansion.
- Multi-extension allowlist enumeration.
- Inline hashing (on-demand SHA256) with toggleable performance budget.


## Deployment Options

### 🏠 **Single Machine** 
- **Local Development**: Direct .NET + React execution
- **Docker Single Container**: All-in-one container

### 🏢 **Team/Production**
- **Docker Microservices**: Separate API, Web, Database, Redis containers
- **Kubernetes**: Full orchestration (advanced)
- **Cloud**: Azure Container Instances, AWS ECS, etc.

## Troubleshooting

### Common Issues

**"External service unavailable"**
- API server not running or wrong port
- Check: `curl http://localhost:5245/healthz` (or `curl http://localhost:5245/api/healthz`)

**Network discovery not finding printers**
- Configure correct IP ranges in settings
- macOS Docker: Use local development instead
- Check printer accessibility: `curl http://YOUR_PRINTER_IP:7125/printer/info`

**Build failures**
- Verify .NET 9.0.302 SDK installed: `dotnet --info`
- Clean rebuild: `dotnet clean && dotnet build`

### Getting Help

1. **Check Documentation**: [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md) or [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md)  
2. **Review Issues**: GitHub Issues for known problems
3. **Check Logs**: Application logs for detailed error information

## Contributing

We welcome contributions! See [CONTRIBUTING.md](CONTRIBUTING.md) for:
- Development environment setup
- Code style guidelines  
- Testing requirements
- Pull request process

## License

This project is licensed under the MIT License - see the LICENSE file for details.
