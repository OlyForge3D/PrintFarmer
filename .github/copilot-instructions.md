# Copilot Instructions for PrintFarmer

## Repository Summary

**PrintFarmer** is a Blazor WebAssembly (standalone) dashboard for managing multiple 3D printers. It supports Moonraker and PrusaLink backends, normalizes camera URLs, resolves hostnames to IPs, and provides live printer status via SignalR real-time updates.

- **Language**: C# with .NET 9
- **Framework**: ASP.NET Core API backend (separate) + Blazor WebAssembly frontend (standalone)
- **Database**: Multi-provider support (SQLite default, SQL Server, PostgreSQL, MySQL)
- **Real-time**: SignalR hubs for live printer status
- **Testing**: xUnit with integration tests using WebApplicationFactory
- **Repository size**: ~81 source files (66 C#, 15 Razor), small-to-medium project

**Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.**

## Essential Build Instructions

⚠️ **CRITICAL**: Always run commands from the `/src` directory, not the repository root.

### Prerequisites
- .NET SDK 9.0 or later (verified working with 9.0.304)
- Windows/macOS/Linux supported

**CRITICAL**: If .NET 9 SDK is not installed, install it first:
```bash
# Download .NET 9.0.304 SDK (exact version required by global.json)
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --version 9.0.304
export PATH="$HOME/.dotnet:$PATH"
```

**Verify setup:**
```powershell
dotnet --info
```

### Bootstrap & Build Process

**NEVER CANCEL builds or long-running commands. Set timeouts appropriately.**

**1. Restore dependencies:**
```powershell
cd ./src
dotnet restore ./farm-web.sln
```
*Note: Restore takes ~41 seconds on first run. Set timeout to 120+ seconds.*

**2. Build solution:**
```powershell
# Debug build (default for development)
dotnet build ./farm-web.sln -c Debug

# Release build
dotnet build ./farm-web.sln -c Release
```
*Note: Debug build takes ~29 seconds. Set timeout to 90+ seconds.*

**3. Run tests:**
```powershell
dotnet test ./farm-web.sln -c Debug
```
*Note: Tests take ~49 seconds and run 51 integration tests. Set timeout to 120+ seconds. NEVER CANCEL.*

**4. Format code:**
```powershell
dotnet format ./farm-web.sln
```
*Note: Formatting takes ~80 seconds. Set timeout to 150+ seconds.*

### Running the Application

**CRITICAL**: This is a two-tier architecture - API backend + separate Blazor WebAssembly frontend.

**API Server (Backend):**
```powershell
cd ./src
dotnet run --project ./api/Farm.Web.Api.csproj
```
- API starts at http://localhost:5245 (http profile)
- HTTPS available at https://localhost:7281
- Health check at http://localhost:5245/health
- API endpoints available at http://localhost:5245/api/*
- Basic health at http://localhost:5245/healthz

**Blazor Client (Frontend) - Run separately:**
```powershell
cd ./src
dotnet run --project ./client/Farm.Web.Client.csproj
```
- Client starts at http://localhost:5000
- Serves the Blazor WebAssembly application
- Connects to API at http://localhost:5245

**Hot reload for active development:**
```powershell
# API server
cd ./src
dotnet watch --project ./api/Farm.Web.Api.csproj run

# Client (separate terminal)
cd ./src
dotnet watch --project ./client/Farm.Web.Client.csproj run
```

### Validation Scenarios

**ALWAYS test actual functionality after making changes:**

1. **API Health Check:**
   ```bash
   curl -s http://localhost:5245/healthz
   # Should return: {"status":"ok"}
   ```

2. **API Endpoints:**
   ```bash
   curl -s http://localhost:5245/api/printers
   # Should return: [] (empty array)
   ```

3. **Comprehensive Health Check:**
   ```bash
   curl -s http://localhost:5245/health
   # Should return detailed health status JSON
   ```

4. **Client Application:**
   ```bash
   curl -s http://localhost:5000/ | head -5
   # Should return HTML with <!DOCTYPE html> and PrintFarmer title
   ```

**Manual Testing Workflow:**
1. Start API server: `dotnet run --project ./api/Farm.Web.Api.csproj`
2. Start client: `dotnet run --project ./client/Farm.Web.Client.csproj`
3. Verify API health: `curl http://localhost:5245/healthz`
4. Verify client: `curl http://localhost:5000/`
5. Test SignalR hub connection and printer status updates

### Common Build Issues & Solutions

1. **.NET Version Mismatch**: Project requires .NET 9.0 SDK. If you get "NETSDK1045" errors about unsupported .NET 9.0, install .NET 9 SDK from https://dot.net/download.

2. **Docker Build Issues**: The main Dockerfile may reference outdated "server" directory paths. The current structure uses "api" directory. If Docker build fails with "server: not found", the Dockerfile needs updating to reference "api" instead of "server".

3. **Migration Warnings**: The app may show migration warnings on first run, but will automatically fall back to EnsureCreated. This is expected behavior for development.

4. **Locked files on Windows**: Close running instances before rebuild:
   ```powershell
   # Clean rebuild if needed
   rd /s /q ./src/client/bin; rd /s /q ./src/client/obj
   rd /s /q ./src/api/bin; rd /s /q ./src/api/obj
   rd /s /q ./src/shared/bin; rd /s /q ./src/shared/obj
   dotnet restore ./farm-web.sln; dotnet build ./farm-web.sln -c Debug
   ```

5. **Database initialization**: The app includes automatic database safety migrations on startup. No manual database setup required.

## Project Architecture & Layout

**IMPORTANT**: This is NOT a "hosted" Blazor WebAssembly project. It's a separate API + client architecture.

```
/
├── CONTRIBUTING.md          # Detailed contributor guidelines (NOTE: Contains outdated references to "server/")
├── README.md               # Basic project overview (NOTE: Contains outdated references)
├── global.json             # .NET SDK version (9.0.304)
├── docker-compose.yml      # Multi-container deployment
├── test-providers.sh       # Database provider testing script
└── src/                    # ⚠️ WORKING DIRECTORY FOR ALL COMMANDS
    ├── farm-web.sln        # Solution file
    ├── api/                # ASP.NET Core API server (STANDALONE backend)
    │   ├── Controllers/    # REST API controllers
    │   ├── Services/       # Background services, HTTP clients
    │   ├── Hubs/           # SignalR hubs
    │   ├── Data/           # EF Core DbContext
    │   ├── Migrations/     # EF Core migrations
    │   ├── Properties/launchSettings.json  # Launch configuration (ports 5245/7281)
    │   ├── appsettings.json # App configuration
    │   └── Program.cs      # Server entry point + startup (API-only, no static files)
    ├── client/             # Blazor WebAssembly client (STANDALONE frontend)
    │   ├── Pages/          # Razor pages/components
    │   ├── Services/       # Client-side services
    │   ├── wwwroot/        # Static assets (CSS, JS, icons)
    │   └── Program.cs      # Client entry point
    ├── shared/             # DTOs and models shared between client/server
    ├── tests/              # Integration tests
    │   └── Farm.Web.Api.Tests/
    └── tools/IconGen/      # Utility tool for icon generation
```

**Architecture Note:** Some documentation (README.md, CONTRIBUTING.md) still references a "server/" directory and describes this as a "hosted" Blazor app. This is outdated - the current structure uses separate "api/" (backend) and "client/" (frontend) projects that run independently.

### Key Architectural Components

**Server Architecture:**
- **Controllers**: REST API endpoints for printers, catalog, Spoolman integration
- **SignalR Hubs**: Real-time printer status updates (`PrinterHub`)
- **Background Services**: `MoonrakerSubscriptionService` for live updates
- **HTTP Clients**: Separate clients for Moonraker, PrusaLink, and SDCP APIs using Refit
- **Database**: Multi-provider support with automatic schema safety checks and migrations
- **Network Discovery**: Hostname resolution and IP normalization

**Client Architecture:**
- **Blazor WebAssembly**: Standalone SPA that runs in browser
- **Pages**: Razor components for UI (Printers management, etc.)
- **SignalR Client**: Connects to API server hubs for real-time updates
- **Configuration**: Loads API base URL from appsettings.json

**Data Flow:**
1. Client UI (http://localhost:5000) → Server API (http://localhost:5245) → Database (CRUD operations)
2. Server Background Service → External Printer APIs → SignalR Hub → Client (real-time status)

**Database Providers:**
- **SQLite** (default): File-based, no setup required
- **SQL Server**: Enterprise database (default in Docker)
- **PostgreSQL**: Advanced open-source database
- **MySQL**: Popular open-source database
- Provider selection via `DB_PROVIDER` environment variable

### Configuration Files

- `src/api/appsettings.json` - Database connections, logging, multi-provider config
- `src/api/Properties/launchSettings.json` - Development server settings (ports 5245/7281)
- `src/farm-web.sln` - Solution configuration
- `global.json` - .NET SDK version requirement (9.0.304)
- Project files: `*.csproj` in each directory

### Dependencies & External Services

**Key NuGet Packages:**
- `Microsoft.EntityFrameworkCore.Sqlite/.SqlServer/.Postgres/.MySql` - Multi-database ORM
- `Microsoft.AspNetCore.SignalR` - Real-time communication
- `Refit.HttpClientFactory` - HTTP API clients
- `Microsoft.AspNetCore.Components.WebAssembly` - Blazor hosting
- `FluentValidation.AspNetCore` - Input validation
- `xunit`, `FluentAssertions`, `Microsoft.AspNetCore.Mvc.Testing` - Testing

**External APIs:**
- Moonraker API (Klipper 3D printer firmware)
- PrusaLink API (Prusa 3D printer firmware)
- SDCP (Simple Data Communication Protocol)

### Validation & Testing

**Pre-commit checks:**
1. Build succeeds: `dotnet build ./farm-web.sln -c Debug`
2. Tests pass: `dotnet test ./farm-web.sln -c Debug`
3. Code formatted: `dotnet format ./farm-web.sln`

**Test Structure:**
- Integration tests in `src/tests/Farm.Web.Api.Tests/`
- Uses `CustomWebApplicationFactory` for testing
- Tests API endpoints, database operations, and health checks
- Tests run against temporary SQLite database (in-memory)
- Total: 51 tests covering core functionality (verified working)

**Manual Verification:**
1. API server starts successfully at http://localhost:5245 (Development profile)
2. Client starts successfully at http://localhost:5000 (Development profile)  
3. Health check endpoints respond:
   - http://localhost:5245/health (comprehensive)
   - http://localhost:5245/healthz (basic)
4. API endpoints accessible (e.g., http://localhost:5245/api/printers)
5. Client serves Blazor WebAssembly app with PrintFarmer title
6. Database initializes automatically (creates `farm.db` file)
7. Application seeds default manufacturers and printer models on first run
8. SignalR hub available at http://localhost:5245/hubs/printers

## Development Guidelines

**Code Style:**
- C#: PascalCase for types/members, camelCase for locals/parameters
- Follow conventional .NET patterns
- Run `dotnet format` before committing

**Entity Framework:**
- Migrations are applied automatically on startup
- Database safety checks handle missing columns
- Multi-provider support: SQLite (default), SQL Server, PostgreSQL, MySQL
- Provider selection via `DB_PROVIDER` environment variable
- Connection strings configured in appsettings.json or via environment variables
- No manual migration commands needed for typical development

**SignalR:**
- Background service disabled during testing environment
- Real-time updates flow: External API → Background Service → Hub → Clients

**File Organization:**
- Controllers: Handle HTTP API requests (PrintersController, CatalogController, SpoolmanController)
- Services: Business logic and external API integration (MoonrakerClient, PrusaLinkClient, etc.)
- Hubs: SignalR real-time communication (PrinterHub)
- Data: Entity Framework models and DbContext
- Shared: Models/DTOs used by both client and server
- Configuration: AppSettings validation and multi-database configuration

**Docker Support:**
- Multi-stage Dockerfile for production builds
- Multi-container setup: separate API and web (Nginx) containers
- Database provider testing scripts: `test-providers.sh`, `test-providers-simple.sh`
- Docker Compose with database services for testing
- ⚠️ **Note**: Main Dockerfile may need updates to reference "api" directory instead of "server"

**Database Provider Testing:**
- Use `./test-providers-simple.sh` to test all database providers
- Automated scripts handle Docker database setup and testing
- Supports testing SQLite, PostgreSQL, SQL Server, and MySQL configurations

**Key API Endpoints:**
- `GET /api/printers` - List all printers
- `POST /api/printers` - Add a new printer
- `PUT /api/printers/{id}` - Update printer configuration
- `DELETE /api/printers/{id}` - Remove a printer
- `GET /health` - Comprehensive health check with detailed status
- SignalR Hub: `/hubs/printers` - Real-time printer status updates

**Trust These Instructions:**
These instructions have been thoroughly tested and validated with .NET 9.0.304. Only search for additional information if these instructions are incomplete or you encounter errors not covered here. The build process, test execution, and development workflow have all been verified to work correctly.

## Critical Timeout Settings & Build Times

**NEVER CANCEL builds or long-running operations. Always set appropriate timeouts:**

| Command | Typical Time | Minimum Timeout | Notes |
|---------|--------------|-----------------|-------|
| `dotnet restore ./farm-web.sln` | ~41 seconds | 120 seconds | First run downloads packages |
| `dotnet build ./farm-web.sln -c Debug` | ~29 seconds | 90 seconds | Includes compilation warnings |
| `dotnet test ./farm-web.sln -c Debug` | ~49 seconds | 120 seconds | Runs 51 integration tests |
| `dotnet format ./farm-web.sln` | ~80 seconds | 150 seconds | Formats entire solution |
| API server startup | ~15 seconds | 60 seconds | Database initialization |
| Client startup | ~10 seconds | 30 seconds | Blazor WebAssembly build |

**CRITICAL WARNINGS:**
- **NEVER CANCEL** commands that appear to hang - they are processing
- Build warnings are normal - build will still succeed
- Database warnings on first run are expected
- Set bash timeouts to at least 50% longer than typical times shown above

## Complete Working Example

**Full development workflow from fresh clone:**

```bash
# 1. Ensure .NET 9.0.304 is installed
dotnet --info  # Should show 9.0.304

# 2. Navigate to working directory
cd ./src

# 3. Restore dependencies (41 seconds, set timeout 120+)
dotnet restore ./farm-web.sln

# 4. Build solution (29 seconds, set timeout 90+)
dotnet build ./farm-web.sln -c Debug

# 5. Run tests (49 seconds, set timeout 120+)
dotnet test ./farm-web.sln -c Debug

# 6. Format code (80 seconds, set timeout 150+)
dotnet format ./farm-web.sln

# 7. Start API server (separate terminal)
dotnet run --project ./api/Farm.Web.Api.csproj
# Wait for: "Now listening on: http://localhost:5245"

# 8. Start client (separate terminal)
dotnet run --project ./client/Farm.Web.Client.csproj
# Wait for: "Now listening on: http://localhost:5000"

# 9. Validate everything works
curl -s http://localhost:5245/healthz        # Should return: {"status":"ok"}
curl -s http://localhost:5245/api/printers   # Should return: []
curl -s http://localhost:5000/ | head -5     # Should show HTML with PrintFarmer
```

**Expected total time for fresh setup:** ~3-4 minutes (excluding .NET SDK installation)
