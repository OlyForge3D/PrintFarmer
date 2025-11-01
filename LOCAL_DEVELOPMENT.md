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

### GCODE_LIBRARY_ROOT (optional)

When set, the backend overrides the physical root used by the `/api/gcode-files` virtual file browser. A `gcode-library` subfolder is created under this path. This allows:

* Using an external or mounted volume for large G-code collections
* Keeping bulky test fixtures out of the repo
* Deterministic, isolated integration tests (the test suite sets this variable)

Example usage:
```bash
export GCODE_LIBRARY_ROOT="$HOME/printfarmer-library"
mkdir -p "$GCODE_LIBRARY_ROOT/gcode-library"
dotnet run --project src/api/Farm.Web.Api.csproj
```

Directory layout:
```
$GCODE_LIBRARY_ROOT/
	gcode-library/
		example1.gcode
		subdir/
			example2.gcode
```

API pagination & metadata:
`GET /api/gcode-files?page=1&pageSize=50` now returns: `files[], totalFiles, totalSize, page, pageSize, totalPages, totalItems`.

Notes:
* Only immediate children of the requested virtual path are listed (no recursion).
* Only `.gcode` files are included today.
* Setting this variable does not affect the database-stored G-code metadata paths already persisted.
* `pageSize` is clamped to a maximum of **500** to prevent excessive payloads.
* Directory deletion is intentionally not supported: if **all** provided paths resolve to directories the delete request returns `400`. Mixed file + directory batches now return `200` with directories reported under `failed`.
* The download endpoint (`GET /api/gcode-files/download?path=/example.gcode`):
	* Supports `HEAD` requests for lightweight existence checks and metadata (ETag/Last-Modified headers returned, no body).
	* Returns `ETag` and `Last-Modified` headers for cache validation.
	* Honors conditional headers `If-None-Match` and `If-Modified-Since` (with a 1s tolerance), responding with `304 Not Modified` when appropriate.
	* Strong ETag (default) combines last modified ticks + file size. Set `GCODE_WEAK_ETAGS=1` to emit a weak validator (`W/` prefix) for future scenario flexibility.

Delete response contract (example):
```
{
  "requested": ["/part1.gcode","/folder","/missing.gcode"],
  "deletedFiles": ["/part1.gcode"],
  "skipped": ["/missing.gcode"],
  "failed": ["/folder"],
  "totalRequested": 3,
  "totalSucceeded": 1,
  "totalSkipped": 1,
  "totalFailed": 1
}
```

Environment variable summary:
* `GCODE_LIBRARY_ROOT` – Override physical storage root.
* `GCODE_WEAK_ETAGS=1` – Enable weak ETag emission for download/HEAD responses.

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
- **Node.js >=20.19 (recommend v20.19.0)** and npm (for React frontend)
- **Git** for source control

### Additional macOS Requirements
- **Homebrew** - Package manager for installing development tools
- **GNU Coreutils** - Provides `timeout` command required by build scripts

**Note:** The automated setup script will install Homebrew and GNU coreutils automatically if missing.

### Apple Silicon (M1/M2) Docker notes

On Apple Silicon (arm64) Macs some prebuilt Docker images or native toolchains used by the build (for example certain native build tools or precompiled binaries) may only be available for linux/amd64. To avoid runtime and build-time failures you can force Docker to use the amd64 platform when building or pulling images.

Recommended short guidance:

```bash
# Use the amd64 platform for builds and pulls when on Apple Silicon
export DOCKER_DEFAULT_PLATFORM=linux/amd64

# Build the API runtime image (example)
DOCKER_DEFAULT_PLATFORM=linux/amd64 docker build \
	--progress=plain -f scripts/docker/dockerfiles/Dockerfile.multistage \
	--target api-runtime -t printfarmer-api-multistage:local .

# Or set the env var globally for the shell session
export DOCKER_DEFAULT_PLATFORM=linux/amd64
docker compose --env-file .env.microservices -f scripts/docker/compose-templates/docker-compose.microservices.yml up -d
```

Notes:
- For development you can set `DOCKER_DEFAULT_PLATFORM` in your shell profile (`~/.zshrc`) while working on PrintFarmer. Remember to unset it if you switch back to native-arm builds for other projects.
- The deploy script respects `TARGETPLATFORM` and `DOCKER_DEFAULT_PLATFORM` build args where applicable; consult `scripts/docker/dockerfiles/Dockerfile.multistage` for details.

### Verify Installation
```bash
# Check .NET version (should show 9.0.302)
dotnet --info

# Check Node.js version (should be >=20.19, e.g. v20.19.0)
node --version
npm --version
```

### One-line bootstrap commands (per OS)

If you want a quick, one-line way to install the required dependencies on a fresh machine, use the appropriate command below. These call the helper scripts in `scripts/` and are the recommended starting point.

- Ubuntu (run as root):

```bash
sudo bash ./scripts/bootstrap-ubuntu.sh
```

- macOS (run as your user; Homebrew will be installed if missing):

```bash
bash ./scripts/bootstrap-macos.sh
```

- Windows (PowerShell as Administrator):

```powershell
.\scripts\bootstrap-windows.ps1
```

Windows options and recommended usage

If you prefer the script to automatically re-launch elevated (skip the interactive prompt), pass the `-ForceElevated` or `--elevate` flag. Example:

```powershell
# Re-run the script elevated immediately (skips prompt)
.\scripts\bootstrap-windows.ps1 -ForceElevated

# Or perform installs but also run verification afterwards
.\scripts\bootstrap-windows.ps1 -ForceElevated -Verify
```

## Use Devcontainer (recommended for contributors)

If you use VS Code, the included `.devcontainer/` configuration provides a reproducible development environment with Node.js, .NET 9.0.302, Docker/Docker-in-Docker support and the workspace post-create provisioning scripted in `.devcontainer/post-create.sh`.

Quick steps:

1. Open the repo in VS Code.
2. When prompted to "Reopen in Container", accept. If not prompted, open the command palette (Ctrl+Shift+P) and run: "Remote-Containers: Reopen in Container".
3. The container will run the `postCreateCommand` which restores the .NET solution, runs `npm ci` for the React app, installs common dotnet global tools, and creates helpful aliases.

Optional verification: re-open the container using the `--verify` flag or set the env var `DEVCONTAINER_VERIFY=1` to run lightweight smoke-tests during provisioning:

```bash
# From your host (when using devcontainer CLI or launching via VS Code), set env var before reopening
export DEVCONTAINER_VERIFY=1
# Or pass the flag to the post-create script (when iterating locally inside container)
.devcontainer/post-create.sh --verify
```

Benefits of using the devcontainer:

- Reproducible environment across contributors and CI.
- Pinned .NET SDK matching `global.json` and preinstalled Node.js/npm.
- Ports for the API (5245) and Vite (3000) are forwarded automatically.

## Troubleshooting checklist (devcontainer & devices)

If you encounter issues running PrintFarmer inside the devcontainer, try these quick checks:

- Networking / forwarded ports:
	- Verify VS Code forwarded ports (view `Ports` in the Remote-Containers panel) and ensure 5245 (API) and 3000 (Vite) are forwarded to your localhost.
	- Use `curl http://localhost:5245/healthz` from your host and from inside the container (`devcontainer exec -- curl ...`) to confirm both sides.

- SignalR / WebSocket connectivity:
	- Confirm the Vite dev server is proxying to `http://localhost:5245` (container env VITE_API_BASE_URL is set). If the client cannot connect to `/hubs/printers` check browser console for CORS or endpoint errors.
	- If SignalR cannot connect from the host to container, ensure `requireLocalPort: false` is set or that the forwarded port is open on the host.

- Device access (USB/camera/printer discovery):
	- Containers generally do not get direct access to USB devices by default. For cameras or USB printers you may need to run the container with `--device` flags or use host networking in a dedicated Docker run.
	- For network discovery (mDNS/SSDP) behavior can be limited inside containers. If device discovery is required, prefer running the API on the host or use a VM with bridged networking.

- File permissions & volumes:
	- Confirm the `node_modules` volume mount (`printfarmer-node-modules`) is mounted correctly; if you see module resolution problems, try deleting the docker volume and running `npm ci` again inside the container.

- Devcontainer provisioning failures:
	- Reopen the container (Command Palette → Remote-Containers: Reopen in Container) to re-run `postCreateCommand`.
	- Inspect the container logs (VS Code `Dev Containers` output) and the `.devcontainer/post-create.sh` output for errors.

- If all else fails:
	- Try the host bootstrap (Ubuntu/macOS/Windows) on a local VM or machine. The host scripts now detect when you are inside a devcontainer and will skip redundant installs.


If you run the script without elevation it will prompt you with options to re-run elevated, continue and elevate individual commands as-needed, or exit. The `-Verify` / `--verify` flag runs small verification checks (dotnet/node/npm/git) and a small dotnet build smoke-test when possible.



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

**Install Node.js >=20.19 (if needed):**
Prefer installing via nvm so contributors can switch Node versions per-project without affecting system packages.

```bash
# Install nvm (if not already installed) and use it to install Node 20.19.0 (recommended)
curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.39.4/install.sh | bash
export NVM_DIR="$HOME/.nvm"
source "$NVM_DIR/nvm.sh"
nvm install 20.19.0
nvm use 20.19.0

# macOS Homebrew alternative (not recommended over nvm):
# brew install node@20

# Or download from: https://nodejs.org/ (choose v20.19.0 or later)
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

### Multi-Database Providers & Validation
While SQLite is the default for speed, the codebase supports **PostgreSQL**, **SQL Server**, and **MySQL** with identical catalog behavior (manufacturers & models) validated via integration tests (Sept 2025):

| Provider | Status | Notes |
|----------|--------|-------|
| SQLite   | ✅ | Default & fastest for local dev |
| PostgreSQL | ✅ | Full catalog test pass |
| MySQL    | ✅ | Full catalog test pass |
| SQL Server | ✅ | Full catalog test pass (health probe may show "unhealthy" under emulation but connections succeed) |

Schema creation currently relies on `EnsureCreated()` (no migrations yet during soft-freeze). Shadow lowercase columns (`NameLowered`) + unique indexes are created automatically to enforce case-insensitive uniqueness across providers. Once the freeze lifts, formal EF Core migrations will replace this bootstrap approach.

To temporarily switch providers for local testing (example PostgreSQL running on localhost or Docker network):
```bash
export DB_PROVIDER=Postgres
export ConnectionStrings__Default="Host=localhost;Database=printfarmer;Username=dev;Password=devpass"
dotnet run --project api/Farm.Web.Api.csproj
```
**Note:** All providers use the unified `ConnectionStrings__Default` environment variable. The connection string format varies based on the provider.

Fallback: if `DB_PROVIDER` is unset or unsupported, the application silently uses SQLite.

### Catalog Normalization & Duplicate Handling (Local Dev)
The catalog layer normalizes names on create/update and returns the canonical value via the `X-Normalized-Name` response header. Duplicate submissions (including case-only differences or extra whitespace) return `409 Conflict` with ProblemDetails and still include the header so you can auto-correct client state.

Key points for developers:
* Normalization trims, squashes interior excess whitespace, and applies canonical casing rules (see `CatalogNameNormalizer`).
* Case-insensitive uniqueness: enforced both in-memory (for friendly 409) and at the DB layer (shadow `NameLowered` unique indexes).
* List endpoints emit weak ETags (`W/"hash"`); include `If-None-Match` to receive `304 Not Modified` and avoid redundant payloads.
* GET-by-id endpoints are available for manufacturers and models; prefer them after a create to re-fetch server state if needed.

Quick manual test (using httpie or curl):
```bash
# Create (untrimmed, odd spacing)
curl -s -D - -X POST http://localhost:5245/api/catalog/manufacturers \
	-H 'Content-Type: application/json' \
	-d '{"name":"  prUsA  "}' | jq '.'

# Repeat (case/spacing difference) should 409
curl -s -D - -o /dev/null -X POST http://localhost:5245/api/catalog/manufacturers \
	-H 'Content-Type: application/json' \
	-d '{"name":"PRUSA"}' | grep -i '^http\|^x-normalized-name\|^content-type'

# List with ETag then conditional GET
etag=$(curl -s -D - http://localhost:5245/api/catalog/manufacturers | awk '/^etag:/ {print $2}')
curl -s -o /dev/null -w '%{http_code}\n' -H "If-None-Match: $etag" http://localhost:5245/api/catalog/manufacturers
```

Client guidance during development:
1. Always check `X-Normalized-Name` after create/update; update UI if different.
2. Treat 409 as “duplicate after normalization” and surface the canonical form to the user.
3. Use caching headers in the React app to reduce network chatter when refetching catalog lists.

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
