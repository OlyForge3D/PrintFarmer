# PrintFarmer

![CI](https://github.com/jpapiez/PrintFarmer/actions/workflows/ci.yml/badge.svg)
![Containers](https://github.com/jpapiez/PrintFarmer/actions/workflows/containers.yml/badge.svg)
![Dependency Review](https://github.com/jpapiez/PrintFarmer/actions/workflows/dependency-review.yml/badge.svg)
![Codecov](https://img.shields.io/codecov/c/github/jpapiez/PrintFarmer)
![CodeQL](https://github.com/jpapiez/PrintFarmer/actions/workflows/codeql.yml/badge.svg)

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
✅ **CSV Import/Export** - Bulk printer configuration management  
✅ **Multi-Database Support** - SQLite, PostgreSQL, SQL Server, MySQL (all tested)  
✅ **Production Ready** - Docker deployment, health checks, comprehensive monitoring

## 🚀 Quick Start (2 minutes)

### Option 1: Docker Deployment (Recommended for Production)

```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
./scripts/deploy-docker.sh --non-interactive
```

Open **http://localhost** in your browser.

**Raspberry Pi / ARM64 deployment?** Use monolith mode with a single container:
```bash
export DEPLOYMENT_MODE=monolith
export DB_PROVIDER=sqlite
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
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer/src

# Restore dependencies
dotnet restore ./farm-web.sln
cd ./Web/ReactApp && npm install && cd ../../

# Build
dotnet build ./farm-web.sln -c Debug

# Terminal 1: Start API server
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
Database: SQLite / PostgreSQL / SQL Server / MySQL
```

### Technology Stack

**Backend:**
- ASP.NET Core 10 (.NET SDK 10.0)
- Entity Framework Core (multi-database ORM)
- SignalR (real-time WebSocket communication)
- Refit (type-safe HTTP clients)
- xUnit (testing framework)

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
- **[Database Guide](./docs/DATABASE.md)** - Schema, migrations, multi-provider support

**Operations:**
- **[Deployment Guide](./docs/DEPLOYMENT.md)** - Docker, environments, configuration
- **[Development Guide](./docs/DEVELOPMENT.md)** - Code style, testing, contribution workflow
- **[Troubleshooting Guide](./docs/TROUBLESHOOTING.md)** - Common issues and solutions

**Quick reference:**
- **[Quick Reference](./docs/QUICK_REFERENCE.md)** - Commands, common tasks
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

# MySQL
DB_PROVIDER=mysql DB_CONNECTION_STRING="Server=localhost;Database=printfarmer;Uid=root;Pwd=password"
```

## 🧪 Testing

All tests pass and are automated:

```bash
# Backend tests
cd ./src
dotnet test ./farm-web.sln -c Debug
# ✅ 1572/1572 API tests passing

# Frontend tests
cd ./src/Web/ReactApp
npm run test:run
# ✅ 365/365 React tests passing
```

## 🐳 Deployment

### Docker Deployment Modes

**Monolith Mode** (single container, perfect for Raspberry Pi):
```bash
export DEPLOYMENT_MODE=monolith
export DB_PROVIDER=sqlite
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
- ✅ All database providers (SQLite, PostgreSQL, MySQL)

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
export DB_PROVIDER=sqlite
./scripts/deploy-docker.sh --non-interactive

# Or use pre-built image
docker pull ghcr.io/olyforge3d/printfarmer-monolith:latest
docker run -d -p 5000:5000 \
  -e DB_PROVIDER=sqlite \
  -e DEPLOYMENT_MODE=monolith \
  -v printfarmer-data:/app/data \
  ghcr.io/olyforge3d/printfarmer-monolith:latest
```

**Minimum specs:** Raspberry Pi 4 (8GB RAM) recommended. Pi 5 ideal.

For complete Pi hardware recommendations, setup checklist, and troubleshooting, see **[Deployment Hardware Guide](./docs/DEPLOYMENT_HARDWARE.md)** (includes cost analysis, network configuration, and performance tuning).

## 🔒 Security

- **Authentication**: JWT tokens with secure HttpOnly cookies
- **Authorization**: Role-based access control (Admin, Operator, Viewer)
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

### Git Hooks (Optional)

```bash
./.githooks/setup.sh
```

This installs pre-commit hooks for local linting (ShellCheck, yamllint, path casing, ESLint, dotnet format). Checks run only on staged files for speed. CI workflows remain the server-side enforcement.

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

This project is licensed under the MIT License - see [LICENSE](./LICENSE) file for details.

## 🙏 Acknowledgments

PrintFarmer builds on amazing open-source projects:
- [Moonraker](https://github.com/mainsail-crew/moonraker) - Klipper firmware
- [OrcaSlicer](https://github.com/SoftFever/OrcaSlicer) - Advanced slicing
- [PrusaLink](https://github.com/prusa3d/PrusaLink) - Prusa integration
- React, ASP.NET Core, and the broader .NET/JavaScript ecosystems

## 📧 Support

- 📖 **[Complete Documentation](./docs/)**
- 🐛 **[GitHub Issues](https://github.com/jpapiez/PrintFarmer/issues)**
- 💬 **[GitHub Discussions](https://github.com/jpapiez/PrintFarmer/discussions)**
- 🔒 **[Security Issues](./SECURITY.md)**

---

**Last Updated:** January 11, 2026  
**Current Version:** See [GitHub Releases](https://github.com/jpapiez/PrintFarmer/releases)  
**Current Phase:** Phase 4 - COMPLETE (Phase 4.5 Load Balancing planned next)
