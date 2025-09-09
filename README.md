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
- **Flexible Database**: SQLite, PostgreSQL, SQL Server, MySQL support
- **Docker Ready**: Production deployment with containers
- **WiFi Friendly**: Works with WiFi-connected printers (local development)
- **Signed Images**: Cosign-signed container images (keyless OIDC or key-based)
- **Provenance Attestations**: SLSA provenance tying digests to build workflow

## Quick Start - Choose Your Path

### 🚀 **Automated Docker Deployment (Recommended for Production)**
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
chmod +x scripts/deploy-docker.sh
./scripts/deploy-docker.sh
```
The script will guide you through configuration and deploy everything automatically.

### 💻 **Local Development (Recommended for Development)**
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
chmod +x scripts/setup-local.sh
./scripts/setup-local.sh
```
Direct development on your machine without containers.

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

Base runtime image layering:
1. `Dockerfile.slicer-base` – Neutral hardened runtime (GTK/offscreen deps, non-root user, health infra)
2. Engine Dockerfile – Adds engine binary + worker application (entrypoint provided by project)

Removed legacy components:
- Generic `slicer-worker` project (superseded by per-engine workers)
- Historical `Dockerfile.base` (replaced by `Dockerfile.slicer-base`)

To add a new engine worker:
1. Create a new project `Farm.<EngineName>Slicer.Worker` modeled after existing workers.
2. Add a Dockerfile similar to `Dockerfile.orcaslicer` layering on `Dockerfile.slicer-base`.
3. Register it in `docker-compose.microservices.yml` and add its URL under `SlicerOrchestrator__Workers__<EngineName>` in the API service environment.
4. Define a distinct Redis queue name (e.g., `<engine>-jobs`).

Shared environment variables (ports, Redis, storage endpoint, queue naming) are documented here:
**➡️ [Shared Worker Environment Variables](docs/slicer/worker-environment.md)**

Port mapping: each worker listens internally on `8080`; external host ports (e.g. `8081` Orca, `8082` Prusa) are defined by compose/Kubernetes configuration, not by changing `ASPNETCORE_URLS` inside the image.

Health & readiness endpoints: each worker exposes `/healthz` (liveness) and readiness via the same endpoint (engine initialization performs binary detection early and fails fast if missing).

Graceful shutdown: workers finish active jobs then exit (shutdown timeout managed by host/container orchestrator; future enhancement could add configurable timeout via `WORKER_SHUTDOWN_TIMEOUT`).


## Detailed Documentation

**� [Deployment Overview](DEPLOYMENT_OVERVIEW.md)** - Choose the right deployment approach for your needs  
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
- `GET /api/printers` — List all configured printers  
- `POST /api/printers` — Add a new printer
- `POST /api/printers/discover-streaming` — Real-time network discovery
- `GET /api/network-discovery/settings` — Network discovery configuration
- **SignalR Hub**: `/hubs/printers` — Real-time printer status updates

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

## Development Workflow

### Local Development (Recommended)
1. **API Backend**: `dotnet run --project api/Farm.Web.Api.csproj` 
2. **React Frontend**: `cd Web/ReactApp && npm run dev`
3. **Open**: http://localhost:3000 (auto-connects to API)

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
