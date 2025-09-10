### Quick Monolithic Start (Unified Script)

Use the unified helper script to bootstrap, start, stop, and inspect the local environment.

Initial bootstrap (first time only):
```bash
chmod +x scripts/pf-dev.sh
./scripts/pf-dev.sh bootstrap
```

Start services (background by default):
```bash
./scripts/pf-dev.sh start
```

Check status / show PIDs:
```bash
./scripts/pf-dev.sh status
```

Follow logs (Ctrl+C to exit):
```bash
./scripts/pf-dev.sh logs --follow
```

Stop services:
```bash
./scripts/pf-dev.sh stop
```

Run tests quickly:
```bash
./scripts/pf-dev.sh test
```

All legacy local helper scripts have been consolidated; use only `scripts/pf-dev.sh` going forward.

Environment variables you can override before running (apply to `pf-dev.sh start`):

| Variable | Default | Purpose |
|----------|---------|---------|
| DEPLOYMENT_MODE | monolithic | Enables SPA proxy logic |
| SPA_DEV_URL | http://localhost:3000 | Vite dev server URL to probe/proxy |
| SPA_PROXY_PROBE_TIMEOUT_MS | 500 | Probe timeout (ms) before skipping proxy |
| ALLOWED_ORIGINS | http://localhost:3000 | CORS origin for React dev server |
| ASPNETCORE_URLS / API_URL | http://localhost:5245 | API listen address |

If the dev server is not yet ready the backend logs `[SPA]` warning and you can just refresh once Vite finishes starting.

#### Script Commands Summary
| Command | Purpose |
|---------|---------|
| bootstrap | Restore dotnet + npm dependencies; create .env if missing |
| start | Launch API + React (background) |
| start -f / --foreground | Launch and block until Ctrl+C |
| status | Show running PIDs and URLs |
| logs [--follow] | Show (or follow) recent logs |
| stop | Stop running processes |
| test | Run API + React test suites |
| clean [--deep] | Remove logs/PIDs (and bin/obj with --deep) |
# PrintFarmer - Local Development Guide

This guide covers running PrintFarmer locally on your development machine **without Docker containers**. This is the recommended approach for active development, especially on macOS where Docker networking limitations can prevent WiFi device discovery.

## Prerequisites

### Required Software
- **.NET SDK 9.0+** (exactly 9.0.302 as specified in global.json)
- **Node.js 18+** and npm (for React frontend)
- **Git** for source control

### Additional macOS Requirements
- **Homebrew** - Package manager for installing development tools
- **GNU Coreutils** - Provides `timeout` command required by build scripts

**Note:** The automated setup script will install Homebrew and GNU coreutils automatically if missing.

### Verify Installation
```bash
# Check .NET version (should show 9.0.302)
dotnet --info

# Check Node.js version (should be 18+)
node --version
npm --version
```

### Install Missing Prerequisites

**Install .NET 9.0.302 SDK:**
```bash
# macOS/Linux - using the provided script
cd /Users/jpapiez/s/PrintFarmer
chmod +x dotnet-install.sh
./dotnet-install.sh --version 9.0.302
export PATH="$HOME/.dotnet:$PATH"

# Or download from: https://dotnet.microsoft.com/download/dotnet/9.0
```

**Install Node.js 18+ (if needed):**
```bash
# macOS with Homebrew
brew install node@18

# Or download from: https://nodejs.org/
```

## Architecture Overview

PrintFarmer uses a **two-tier architecture** for local development:

1. **ASP.NET Core API Backend** (localhost:5245) - Handles data, business logic, SignalR hubs
2. **React TypeScript Frontend** (localhost:3000) - User interface, connects to API via HTTP and SignalR

**Important:** Both services must run simultaneously in separate terminals.

## Quick Start

### Step 1: Clone and Navigate
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer/src  # ⚠️ IMPORTANT: Always work from /src directory
```

### Step 2: Restore Dependencies
```bash
# Restore .NET dependencies (takes ~41 seconds first time)
dotnet restore ./farm-web.sln

# Install React dependencies (takes ~30-60 seconds first time)
cd Web/ReactApp
npm install
cd ../../  # Back to src directory
```

### Step 3: Build Projects
```bash
# Build .NET solution (takes ~83 seconds)
dotnet build ./farm-web.sln -c Debug

# Build React application
cd Web/ReactApp
npm run build
cd ../../
```

### Step 4: Run API Server (Terminal 1)
```bash
# From src directory
cd api
dotnet run --project Farm.Web.Api.csproj

# Wait for: "Now listening on: http://localhost:5245"
# The API will create farm.db automatically on first run
```

### Step 5: Run React Client (Terminal 2)
```bash
# From src directory (new terminal)
cd Web/ReactApp
npm run dev

# Wait for: "Local: http://localhost:3000/"
```

### Step 6: Verify Everything Works
```bash
# Test API health (in terminal 3)
curl http://localhost:5245/healthz
# Should return: {"status":"ok"}

curl http://localhost:5245/api/printers
# Should return: []

# Test React client
curl http://localhost:3000/ | head -5
# Should show HTML with PrintFarmer title

# Open browser to: http://localhost:3000
```

## Development Workflow

### Hot Reload Development
For active development with automatic restarts:

**Terminal 1 - API with hot reload:**
```bash
cd PrintFarmer/src
dotnet watch --project api/Farm.Web.Api.csproj run
```

**Terminal 2 - React with hot reload:**
```bash
cd PrintFarmer/src/Web/ReactApp
npm run dev
```

Now changes to C# code or React code will automatically reload the respective services.

### Running Tests
```bash
# .NET API tests (from src directory)
dotnet test ./farm-web.sln -c Debug

# React tests
cd Web/ReactApp
npm test
```

### Code Formatting
```bash
# Format .NET code (takes ~80 seconds)
dotnet format ./farm-web.sln

# Format React code
cd Web/ReactApp
npm run lint
```

## Key Endpoints

### API Server (http://localhost:5245)
- `GET /healthz` (alias: `/api/healthz`) - Basic health check
- `GET /health` (alias: `/api/health`) - Comprehensive health check with detailed status
- `GET /api/printers` - List all configured printers
- `POST /api/printers` - Add a new printer
- `GET /api/network-discovery/settings` - Get network discovery configuration
- `POST /api/printers/discover-streaming` - Start network discovery with real-time updates
- SignalR Hub: `/hubs/printers` - Real-time printer status updates

### React Client (http://localhost:3000)
- Modern React TypeScript application
- Real-time updates via SignalR connection to API
- Responsive design with Tailwind CSS

## Database

PrintFarmer uses **SQLite** by default for local development:
- Database file: `src/api/farm.db` (created automatically)
- No manual setup required
- Database is seeded with default data on first run

## Network Discovery (Local Development Benefits)

**WiFi Device Access:** Unlike Docker containers, local development can directly access WiFi-connected devices:
```bash
# This works in local development but may fail in Docker on macOS
curl -m 5 http://10.0.0.80:7125/printer/info
```

**Real-time Discovery:** SignalR provides live progress updates during network scanning.

## Troubleshooting

### Port Conflicts
If ports 5245 or 3000 are in use:
```bash
# Check what's using the ports
lsof -i :5245
lsof -i :3000

# Kill processes if needed
lsof -ti:5245 | xargs kill -9
lsof -ti:3000 | xargs kill -9
```

### Database Issues
```bash
# Clean database (will lose data)
cd src/api
rm -f farm.db farm.db-shm farm.db-wal

# Restart API server to recreate database
```

### Build Issues
```bash
# Clean rebuild
cd src
rm -rf */bin */obj */*/bin */*/obj
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
```

### Common Error Messages

**"External service unavailable"**
- API server not running or not accessible
- Check API server is running on localhost:5245
- Verify no firewall blocking local connections

**".NET 9.0 SDK not found"**
- Install .NET 9.0.302 SDK (exact version required)
- Check global.json for required version

**"Module not found" (React)**
- Run `npm install` in Web/ReactApp directory
- Check Node.js version is 18+

### Performance Notes

**Expected Build Times:**
- Initial `dotnet restore`: ~41 seconds
- `dotnet build`: ~83 seconds  
- `npm install`: ~30-60 seconds
- `npm run build`: ~20-40 seconds
- `dotnet test`: ~11 seconds
- `npm test`: ~5-10 seconds

**Memory Usage:**
- API server: ~100-200 MB
- React dev server: ~50-100 MB
- Total: ~150-300 MB

## Next Steps

- **Production Deployment:** See [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md) for containerized deployment
- **Contributing:** See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines
- **Network Configuration:** Configure network ranges in the UI for printer discovery

## Local vs Docker Development

**Use Local Development When:**
- Active development and debugging
- Need WiFi device access (especially on macOS)
- Want faster build/test cycles
- Debugging network discovery issues

**Use Docker When:**
- Production deployment
- Consistent environment across team
- Testing containerized deployment
- Deploying on Linux servers
