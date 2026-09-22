# PrintFarmer

![CI](https://github.com/OlyForge3D/PrintFarmer/actions/workflows/ci.yml/badge.svg)
![Containers](https://github.com/OlyForge3D/PrintFarmer/actions/workflows/containers.yml/badge.svg)
![Dependency Review](https://github.com/OlyForge3D/PrintFarmer/actions/workflows/dependency-review.yml/badge.svg)
![Codecov](https://img.shields.io/codecov/c/github/OlyForge3D/PrintFarmer)
![CodeQL](https://github.com/OlyForge3D/PrintFarmer/actions/workflows/codeql.yml/badge.svg)

A **production-ready** React TypeScript dashboard for managing multiple 3D printers with real-time updates, location organization, and integrated slicing capabilities.

## 📚 Quick Links

| What do you want to do? | Documentation |
|------------------------|----------------|
| **Get started quickly** | [Getting Started Guide](./docs/GETTING_STARTED.md) |
| **Understand the system** | [Architecture Overview](./docs/ARCHITECTURE.md) |
| **Choose hardware for your farm** | [Deployment Hardware Guide](./docs/DEPLOYMENT_HARDWARE.md) |
| **Deploy to production** | [Deployment Guide](./docs/DEPLOYMENT.md) |
| **Set up pgAdmin** | [pgAdmin Setup Guide](./docs/PGADMIN_SETUP.md) |
| **Use the API** | [API Reference](./docs/API.md) |
| **Explore features** | [Features Guide](./docs/FEATURES.md) |
| **Contribute code** | [Development Guide](./docs/DEVELOPMENT.md) |
| **Operate Squad automations** | [Shared host-bound Ralph policy](./.github/ralph-reference.md#shared-scheduled-automations) |
| **Fix an issue** | [Troubleshooting Guide](./docs/TROUBLESHOOTING.md) |
| **Browse all docs** | [Documentation Index](./docs/INDEX.md) |

## ✨ Key Features

✅ **Multi-Printer Dashboard** - Manage unlimited 3D printers from a single interface  
✅ **Real-time Updates** - SignalR WebSocket for live status (temperatures, progress, state)  
✅ **Hierarchical Location System** - Organize printers into custom hierarchies (Warehouse > Floor > Room > Rack)  
✅ **Auto-Dispatch with 9-Factor Scoring** - Intelligent job assignment based on material, nozzle, build volume, and more  
✅ **Printer Discovery** - Auto-detect Moonraker and PrusaLink printers on network  
✅ **Automatic Camera Discovery** - Detect and populate camera URLs when importing printers  
✅ **Job Queue Management** - Monitor and control print jobs across all printers  
✅ **Integrated Slicing** - Built-in OrcaSlicer with profile management  
✅ **Printer Calibration Context** - Verified Klipper readiness and credential-free upstream OrcaSlicer snapshots
✅ **Secure Versioned API** - Negotiated contracts, scoped permissions, and truthful operational capabilities
✅ **CSV Import/Export** - Bulk printer configuration management  
✅ **Multi-Database Support** - SQLite, PostgreSQL, and SQL Server
✅ **Production Ready** - Docker deployment, health checks, comprehensive monitoring

### Moonraker motion controls

Homing, jogging, and absolute movement use direct POST requests to the existing
`/home`, `/homexy`, `/homez`, `/move`, and `/moveto` printer routes. A successful
`CommandResult` means ordinary backend acceptance, **not physical completion**.
Controls remain pending only while their request is in flight, with a five-minute
direct-command timeout. Errors and timeouts remain explicit; clients never
automatically replay motion.

Web controls offer **Guided** (the default) and **Expert** presentation. The
selection is saved to your user account on the backend and follows you across
devices, including the printer detail card and sidebar. Guided adds inline
input hints and precautions; Expert keeps those in keyboard- and touch-accessible
help. Neither mode changes permissions, valid-request requirements, firmware
protections, concurrent-command coordination, or emergency-stop access.

The initiating control shows request activity immediately. Failures and
unconfirmed outcomes remain visible in both modes. There are no motion receipts,
operation IDs, persistent journals, result polling, or recovery forms.
Absolute GO requires explicit, finite X, Y, and Z targets in millimeters.
Empty, untouched fields are not errors, and current-position labels do not fill
in omitted targets.

Manual Jog/MoveTo does not require an automated workflow's minimum-clearance
value, which Moonraker cannot authoritatively discover. Fresh XYZ homing,
position/frame and travel-envelope checks still apply, as do Klipper's configured
rules. Automated clearance-protected workflows retain their stricter policy.
This does not enable unhomed-axis movement or out-of-envelope recovery moves.

**Coordinated upgrade required:** stop all old API instances and workers before
backing up and applying the provider migrations; mixed old/new writers are not
supported. Down migrations are intentionally unsupported; rollback requires the
pre-upgrade backup and operator reconciliation before restarting old software.
Update React and iOS alongside the API. All control-operations admission,
receipt, current-state and recovery routes are removed (`404`), as are printer
`physicalControl` fields and the motion-operation SignalR event.

If communication is lost after a command may have been sent, the audit outcome
remains **Unknown**. The shared database barrier still coordinates direct
commands with print dispatch; uncertainty releases manual coordination only
after backend I/O settles. A crashed direct command's expired barrier can be
reclaimed by a new explicit direct request after its timeout plus 45 seconds,
never by replaying the old move or declaring it successful. Print-job ownership
is preserved. Check the physical printer before requesting another move:
released coordination does not prove it stopped.
See [direct manual-motion coordination](./docs/JOB_QUEUE_ARCHITECTURE.md#direct-manual-motion-control).

## 🚀 Quick Start (2 minutes)

### Option 1: Docker Deployment (Recommended for Production)

```bash
git clone https://github.com/OlyForge3D/PrintFarmer.git
cd PrintFarmer
./scripts/deploy-docker.sh --non-interactive
```

Open **http://localhost** in your browser.

**Raspberry Pi / ARM64 deployment?** Use monolith mode with a single container:
```bash
export DEPLOYMENT_MODE=monolith
export DB_PROVIDER=postgres
./scripts/deploy-docker.sh --non-interactive
# Opens http://localhost:5000
```

**Or pull pre-built images from GitHub Container Registry:**
```bash
# Monolith (single container for Pi)
docker pull ghcr.io/olyforge3d/printfarmer-monolith:latest

# Microservices (API + frontend)
docker pull ghcr.io/olyforge3d/printfarmer-api:latest
docker pull ghcr.io/olyforge3d/printfarmer-frontend:latest
```

See **[Deployment Hardware Guide](./docs/DEPLOYMENT_HARDWARE.md)** for hardware recommendations and ARM/Pi setup.

### Option 2: Local Development (Recommended for Development)

```bash
git clone https://github.com/OlyForge3D/PrintFarmer.git
cd PrintFarmer/src

# Restore dependencies
dotnet restore ./farm-web.sln
cd ./Web/ReactApp && npm install && cd ../../

# Build
dotnet build ./farm-web.sln -c Debug

# Terminal 1: Start API server
export WorkerAuth__SharedKey="$(openssl rand -hex 32)"
dotnet run --project ./api/Farm.Web.Api.csproj

# Terminal 2: Start React dev server
cd ./Web/ReactApp
npm run dev
```

Open **http://localhost:3000** in your browser.

See the **[Getting Started Guide](./docs/GETTING_STARTED.md)** for detailed setup instructions.

## 🏗️ Architecture

PrintFarmer uses a **modern two-tier client-server architecture**:

```
React TypeScript Frontend (http://localhost:3000)
    ↕ HTTP REST + WebSocket (SignalR)
ASP.NET Core 10 API Backend (http://localhost:5245)
    ↕ Entity Framework Core ORM
    ↓
Database: SQLite / PostgreSQL / SQL Server
```

### Technology Stack

**Backend:**
- ASP.NET Core 10 (.NET SDK 10.0)
- Entity Framework Core (multi-database ORM)
- SignalR (real-time WebSocket communication)
- Refit (type-safe HTTP clients)
- xUnit (testing framework)

Printer controls delegate protocol commands and support declarations to backend
plugins; shared services retain authorization and operation lifecycle policy.
See [printer control ownership](./docs/ARCHITECTURE.md#printer-control-ownership).

**Frontend:**
- React 19+ with TypeScript
- Vite (build tool)
- Tailwind CSS v4 (styling)
- TanStack React Query (server state management)
- Vitest + React Testing Library (testing)

See the **[Architecture Guide](./docs/ARCHITECTURE.md)** for system design, data flow, and component breakdown.

## 📖 Documentation

**Start here:**
- **[Getting Started](./docs/GETTING_STARTED.md)** - Local dev setup, first run
- **[Architecture](./docs/ARCHITECTURE.md)** - System design with diagrams
- **[Features](./docs/FEATURES.md)** - All capabilities and how to use them

**Implementation details:**
- **[API Reference](./docs/API.md)** - REST endpoints and SignalR events
- **[Design System](./docs/DESIGN_SYSTEM.md)** - UI component library, design tokens, theming
- **[UI Documentation](./docs/UI.md)** - Frontend components and pages

**Operations:**
- **[Deployment Guide](./docs/DEPLOYMENT.md)** - Docker, environments, configuration
- **[Worker Authentication](./docs/WORKER_AUTHENTICATION.md)** - Slicer registry, service, and job-route credentials
- **[Ralph Native Role Setup](./docs/ralph-macos-migration.md)** - Mini coordinator, native Mac/Windows consumers, bounded Squad specialist dispatch, explicit category/model validation, correlated startup recovery and preservation-first package renewal
- **[Development Guide](./docs/DEVELOPMENT.md)** - Code style, testing, contribution workflow
- **[Troubleshooting Guide](./docs/TROUBLESHOOTING.md)** - Common issues and solutions

**Quick reference:**
- **[Documentation Index](./docs/INDEX.md)** - Complete documentation catalog

## 💡 Key Concepts

### Location System

Organize your printers by physical location (workshop, garage, classroom, etc.):

```
Workshop
├── Printer 1 (Moonraker)
├── Printer 2 (PrusaLink)
└── Printer 3 (SDCP)

Garage
├── Printer 4 (Moonraker)
└── Printer 5 (PrusaLink)
```

Use drag-and-drop to assign/reassign printers to locations.

### Real-time Monitoring

All printer status updates via **SignalR WebSocket**:
- Connection status (online/offline)
- Printer state (idle, printing, paused, error)
- Temperatures (current and target)
- Job progress (percentage and time remaining)
- Automatic reconnection if connection drops

### Multi-Database Support

Choose your database without code changes:

```bash
# SQLite (default, file-based)
DB_PROVIDER=sqlite

# PostgreSQL
DB_PROVIDER=postgres DB_CONNECTION_STRING="Host=localhost;Database=printfarmer;User=postgres;Password=password"

# SQL Server
DB_PROVIDER=sqlserver DB_CONNECTION_STRING="Server=localhost;Database=printfarmer;User=sa;Password=YourPassword123"

```

MySQL is unavailable until provider-correct application and slicer migrations
are shipped.

<!--
# Obsolete MySQL example
DB_PROVIDER=mysql DB_CONNECTION_STRING="Server=localhost;Database=printfarmer;Uid=root;Pwd=password"

```

-->

## 🧪 Testing

Daily immutable-image browser validation on Windows uses the
[run-owned native WSL runner](./docs/DAILY_UI_VALIDATION_RUNNER.md), with separate
Moonraker and extended-coverage results, durable evidence and verified teardown.

All tests pass and are automated:

```bash
# Backend tests
cd ./src
dotnet test ./farm-web.sln -c Debug --settings ./vstest.runsettings --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type mini
# ✅ 1572/1572 API tests passing

# Frontend tests
cd ./src/Web/ReactApp
npm run test:run
# ✅ 365/365 React tests passing
```

## 🐳 Deployment

Deployment networking is bridge-only. Stale network-mode settings are rejected;
`--include-discovery` explicitly enables discovery even when saved settings
disable it. See the [deployment networking reference](./docs/DEPLOYMENT_QUICK_REFERENCE.md#-deployment-networking)
for migration and the local-dev-only worker exception.

### Docker Deployment Modes

**Monolith Mode** (single container, perfect for Raspberry Pi):
```bash
export DEPLOYMENT_MODE=monolith
export DB_PROVIDER=postgres
./scripts/deploy-docker.sh --non-interactive
```

**Microservices Mode** (separate API + frontend containers, production-ready):
```bash
# Default configuration (no DEPLOYMENT_MODE needed)
./scripts/deploy-docker.sh
```

### Docker Compose (Single Machine)

```bash
./scripts/deploy-docker.sh
```

### Pre-Built Container Images (GitHub Container Registry)

All images support **x86_64** and **ARM64** architectures:

```bash
# Monolith (API + frontend in one container)
docker pull ghcr.io/olyforge3d/printfarmer-monolith:latest

# Or separate microservices
docker pull ghcr.io/olyforge3d/printfarmer-api:latest
docker pull ghcr.io/olyforge3d/printfarmer-frontend:latest
```

See **[Deployment Hardware Guide](./docs/DEPLOYMENT_HARDWARE.md)** for complete GHCR instructions, hardware requirements, and Pi setup.

### Kubernetes (Microservices)

See **[Deployment Guide](./docs/DEPLOYMENT.md)** for Kubernetes setup.

### Environment Variables

```bash
# Database
DB_PROVIDER=postgres
DB_CONNECTION_STRING=...

# API Server
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:5245

# Security
JWT_SECRET=your-secret-key-here

# Logging
SERILOG_LEVEL=Information
```

### 🍓 ARM / Raspberry Pi Deployment

PrintFarmer runs on ARM64 platforms (Raspberry Pi 4/5, Orange Pi, etc.) with automatic graceful degradation — 3D model file support and slicing are disabled since their native libraries (lib3mf, Assimp) lack ARM builds.

**What works on ARM64:**
- ✅ Full printer fleet management (add, remove, monitor, control)
- ✅ G-code file upload and print job queuing
- ✅ Real-time printer status via SignalR
- ✅ Auto-dispatch and bed-clear confirmation
- ✅ Network discovery and Spoolman integration
- ✅ Analytics, statistics, and reporting
- ✅ Native SQLite development and PostgreSQL Docker deployment

**What's disabled on ARM64:**
- ❌ 3D model file upload (STL, OBJ, STEP, 3MF)
- ❌ Slicing (OrcaSlicer/PrusaSlicer workers)
- ❌ 3D model thumbnail generation

**Recommended for Pi:** Use **monolith mode** (single container) for minimal resource usage:

```bash
# Interactive setup (auto-detects ARM)
./scripts/deploy-docker.sh

# Or silent deployment with monolith mode
export DEPLOYMENT_MODE=monolith
export DB_PROVIDER=postgres
./scripts/deploy-docker.sh --non-interactive

# The deployment script provisions the supported database service,
# credentials, connection string, and persistent storage together.
```

**Minimum specs:** Raspberry Pi 4 (8GB RAM) recommended. Pi 5 ideal.

For complete Pi hardware recommendations, setup checklist, and troubleshooting, see **[Deployment Hardware Guide](./docs/DEPLOYMENT_HARDWARE.md)** (includes cost analysis, network configuration, and performance tuning).

## 🔒 Security

- **Authentication**: JWT tokens with secure HttpOnly cookies
- **Authorization**: Role, `resource:action` permission, owner, farm, and worker-resource checks
- **Encryption**: API keys encrypted at rest
- **HTTPS**: Enforced in production
- **Validation**: Input validation and CORS protection
- **Updates**: Regularly updated dependencies

See **[Security Policy](./SECURITY.md)** for vulnerability reporting.

## 🤝 Contributing

We welcome contributions! See **[Contributing Guide](./CONTRIBUTING.md)** for:
- Code style guidelines
- Testing requirements
- Git workflow and commits
- PR process

### Git Hooks (Strongly Recommended)

```bash
./.githooks/setup.sh
```

This installs two hooks:

- **`pre-commit`** — runs local linting (ShellCheck, yamllint, path casing, ESLint) on staged files.
- **`pre-push`** — runs `dotnet format --verify-no-changes` against the exact outgoing Git tree whenever any `.cs`, `.csproj`, `farm-web.sln`, `.editorconfig`, or `Directory.Build.*` file is changed. Successful verifications are cached by tree + SDK + formatter version, so repeat pushes of the same tree are effectively free.

**`dotnet format` no longer runs in CI.** The pre-push hook is the local format gate. Branch protection still enforces CI build/test/drift checks, but does not independently recheck formatting. See [docs/CI.md](./docs/CI.md) for the full CI architecture.

**Emergency bypass:** `git push --no-verify` skips the pre-push hook (Git's standard emergency escape hatch). Local hooks are not server-enforceable — required CI checks are.

## 📊 Project Status

| Component | Status |
|-----------|--------|
| API Backend | ✅ Build Success (0 errors, 134 warnings) |
| React Frontend | ✅ Build Success (0 TypeScript errors) |
| API Tests | ✅ 1572/1572 passing |
| React Tests | ✅ 365/365 passing |
| Docker Build | ✅ Multi-stage production ready |
| Documentation | ✅ Comprehensive and organized |
| Backend Plugins | ✅ 6 supported (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge, Core) |
| Phase 4 Automation | ✅ COMPLETE (Scheduling, Estimates, Notifications, Smart Retry) |

**Latest Completion:** Discovery Probe Architecture Consolidation (December 21, 2025)
- All discovery probes migrated to respective backend plugins
- Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge, Core plugins fully integrated
- All 1572 API tests passing with consolidated architecture
- Zero circular dependencies between backend plugins

## 📝 License

PrintFarmer is licensed under the
[GNU Affero General Public License v3.0 only](./LICENSE)
(`AGPL-3.0-only`) beginning with v0.2.3. Releases through v0.2.2 retain
their historically applicable terms.

Network users can identify and retrieve the exact corresponding source from
the unauthenticated `GET /api/system/source` endpoint. See
[Licensing, source availability, and provenance](./docs/LICENSING_AND_SOURCE.md)
for operator and contributor procedures. Third-party components retain their
own terms and notices in [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md).

## 🙏 Acknowledgments

PrintFarmer builds on amazing open-source projects:
- [Moonraker](https://github.com/mainsail-crew/moonraker) - Klipper firmware
- [OrcaSlicer](https://github.com/OrcaSlicer/OrcaSlicer) - Advanced slicing
- [PrusaLink](https://github.com/prusa3d/PrusaLink) - Prusa integration
- React, ASP.NET Core, and the broader .NET/JavaScript ecosystems

## 📧 Support

- 📖 **[Complete Documentation](./docs/)**
- 🐛 **[GitHub Issues](https://github.com/OlyForge3D/PrintFarmer/issues)**
- 💬 **[GitHub Discussions](https://github.com/OlyForge3D/PrintFarmer/discussions)**
- 🔒 **[Security Issues](./SECURITY.md)**

---

**Last Updated:** January 11, 2026  
**Current Version:** See [GitHub Releases](https://github.com/OlyForge3D/PrintFarmer/releases)  

Server releases use one [manual Actions workflow](docs/RELEASE_GUIDE.md):
choose stable (`main`) or insider (`development`), enter `X.Y.Z` or
`X.Y.Z-insider.N` matching that source's `VERSION`, and run **Consolidated Release**
on `development` as the owner. It checks the source, builds all six supported
container images and publishes a GitHub release with notes and pinned digests
last. Existing release environment protections remain; there is no custom
allocator, signed-operation or abandonment prerequisite.

Used versions are never overwritten. Failed runs report partial results;
start a fresh dispatch with a new version if its tag already exists.
Publication is **manual-install-only**, not managed-update readiness or permission
to update an installation. Historical tags and remote audit records remain intact.
The retired `scripts/release.sh` and `scripts/publish-to-public.sh` helpers still
exit without publication.
**Current Phase:** Phase 4 - COMPLETE (Phase 4.5 Load Balancing planned next)
