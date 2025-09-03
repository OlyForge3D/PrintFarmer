# Copilot Instructions for PrintFarmer

## Repository Summary

**PrintFarmer** is a React TypeScript dashboard for managing multiple 3D printers. It supports Moonraker and PrusaLink backends, normalizes camera URLs, resolves hostnames to IPs, and provides live printer status via SignalR real-time updates.

- **Languages**: C# with .NET 9 (API), TypeScript with React 19 (Frontend)
- **Framework**: ASP.NET Core API backend + React TypeScript frontend (migrated from Blazor WebAssembly)
- **Database**: Multi-provider support (SQLite default, SQL Server, PostgreSQL, MySQL)
- **Real-time**: SignalR hubs for live printer status
- **Testing**: xUnit with integration tests (API), Vitest with React Testing Library (Frontend)
- **Repository size**: Medium project with comprehensive React migration in progress

**Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.**

## Essential Build Instructions

⚠️ **CRITICAL**: Always run commands from the `/src` directory, not the repository root.

### Prerequisites
- .NET SDK 9.0 or later (verified working with 9.0.304) - for API backend
- Node.js 18+ and npm - for React frontend
- Windows/macOS/Linux supported

**CRITICAL**: If .NET 9 SDK is not installed, install it first:
```bash
# Download .NET 9.0.302 SDK (exact version required by global.json)
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --version 9.0.302
export PATH="$HOME/.dotnet:$PATH"
```

**Verify setup:**
```powershell
dotnet --info
node --version  # Should be 18+
npm --version
```

### Bootstrap & Build Process

**NEVER CANCEL builds or long-running commands. Set timeouts appropriately.**

**1. Restore .NET dependencies:**
```powershell
cd ./src
dotnet restore ./farm-web.sln
```
*Note: Restore takes ~41 seconds on first run. Set timeout to 120+ seconds.*

**2. Install React dependencies:**
```powershell
cd ./src/Web/ReactApp
npm install
```
*Note: npm install takes ~30-60 seconds. Set timeout to 120+ seconds.*

**3. Build .NET solution:**
```powershell
cd ./src
# Debug build (default for development)
dotnet build ./farm-web.sln -c Debug

# Release build
dotnet build ./farm-web.sln -c Release
```
*Note: Debug build takes ~83 seconds. Set timeout to 150+ seconds.*

**4. Build React application:**
```powershell
cd ./src/Web/ReactApp
npm run build
```
*Note: React build takes ~20-40 seconds. Set timeout to 90+ seconds.*

**5. Run tests:**
```powershell
# .NET API tests
cd ./src
dotnet test ./farm-web.sln -c Debug

# React tests
cd ./src/Web/ReactApp
npm test
```
*Note: .NET tests take ~11 seconds for 62 tests. React tests are fast. Set timeout to 60+ seconds each.*

**6. Format code:**
```powershell
# .NET formatting
cd ./src
dotnet format ./farm-web.sln

# React linting
cd ./src/Web/ReactApp
npm run lint
```
*Note: .NET formatting takes ~80 seconds. React linting is fast. Set timeout to 150+ seconds.*

### Running the Application

**CRITICAL**: This is a two-tier architecture - API backend + separate React TypeScript frontend.

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

**React Client (Frontend) - Run separately:**
```powershell
cd ./src/Web/ReactApp
npm run dev
```
- Client starts at http://localhost:3000 (default Vite dev server)
- Serves the React TypeScript application
- Connects to API at http://localhost:5245
- Hot reload enabled for fast development

**Hot reload for active development:**
```powershell
# API server (Terminal 1)
cd ./src
dotnet watch --project ./api/Farm.Web.Api.csproj run

# React client (Terminal 2)
cd ./src/Web/ReactApp
npm run dev
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

4. **React Client Application:**
   ```bash
   curl -s http://localhost:3000/ | head -5
   # Should return HTML with <!DOCTYPE html> and PrintFarmer title
   ```

5. **Catalog API (verify database seeding):**
   ```bash
   curl -s http://localhost:5245/api/catalog/manufacturers | jq length
   # Should return: 8 (default manufacturers seeded)
   ```

**Manual Testing Workflow:**
1. Start API server: `dotnet run --project ./api/Farm.Web.Api.csproj`
2. Start React client: `cd ./src/Web/ReactApp && npm run dev`
3. Verify API health: `curl http://localhost:5245/healthz`
4. Verify React client: `curl http://localhost:3000/`
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

**IMPORTANT**: This is a separate API + React frontend architecture (migrated from Blazor WebAssembly).

```
/
├── CONTRIBUTING.md          # Detailed contributor guidelines
├── README.md               # Basic project overview
├── REACT_MIGRATION_README.md # Comprehensive React migration plan and documentation
├── global.json             # .NET SDK version (9.0.302)
├── docker-compose.yml      # Multi-container deployment
├── test-providers.sh       # Database provider testing script
└── src/                    # ⚠️ WORKING DIRECTORY FOR ALL COMMANDS
    ├── farm-web.sln        # .NET Solution file
    ├── api/                # ASP.NET Core API server (STANDALONE backend)
    │   ├── Controllers/    # REST API controllers
    │   ├── Services/       # Background services, HTTP clients
    │   ├── Hubs/           # SignalR hubs
    │   ├── Data/           # EF Core DbContext
    │   ├── Migrations/     # EF Core migrations
    │   ├── Properties/launchSettings.json  # Launch configuration (ports 5245/7281)
    │   ├── appsettings.json # App configuration
    │   └── Program.cs      # Server entry point + startup (API-only, no static files)
    ├── Web/                # React frontend applications
    │   └── ReactApp/       # React TypeScript application (NEW - replaces Blazor client)
    │       ├── src/
    │       │   ├── components/      # React components
    │       │   ├── contexts/        # React contexts (Auth, etc.)
    │       │   ├── pages/           # Page components
    │       │   ├── services/        # API clients and services
    │       │   ├── types/           # TypeScript type definitions
    │       │   └── utils/           # Utility functions
    │       ├── public/              # Static assets
    │       ├── package.json         # npm dependencies and scripts
    │       ├── vite.config.ts       # Vite configuration
    │       └── tsconfig.json        # TypeScript configuration
    ├── client/             # Blazor WebAssembly client (LEGACY - being replaced by React)
    ├── shared/             # DTOs and models shared between client/server
    ├── tests/              # Integration tests
    │   └── Farm.Web.Api.Tests/
    └── tools/IconGen/      # Utility tool for icon generation
```

**Migration Status:** The project is actively migrating from Blazor WebAssembly to React TypeScript. The new React application is in `src/Web/ReactApp/` and follows modern React development practices with Vite, TypeScript, and comprehensive tooling.

### Key Architectural Components

**Server Architecture:**
- **Controllers**: REST API endpoints for printers, catalog, Spoolman integration
- **SignalR Hubs**: Real-time printer status updates (`PrinterHub`)
- **Background Services**: `MoonrakerSubscriptionService` for live updates
- **HTTP Clients**: Separate clients for Moonraker, PrusaLink, and SDCP APIs using Refit
- **Database**: Multi-provider support with automatic schema safety checks and migrations
- **Network Discovery**: Hostname resolution and IP normalization

**Client Architecture:**
- **React TypeScript**: Modern frontend with Vite build tool and hot reload
- **Components**: Modular React components for UI (Printer management, Dashboard, etc.)
- **SignalR Client**: Connects to API server hubs for real-time updates using @microsoft/signalr
- **State Management**: React Query for server state, React Context for application state
- **Styling**: Tailwind CSS with modern responsive design
- **Configuration**: Vite configuration for development and production builds
- **Testing**: Vitest with React Testing Library for component testing

**Data Flow:**
1. React UI (http://localhost:3000) → Server API (http://localhost:5245) → Database (CRUD operations)
2. Server Background Service → External Printer APIs → SignalR Hub → React Client (real-time status)

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
- `global.json` - .NET SDK version requirement (9.0.302)
- Project files: `*.csproj` in each directory

### Dependencies & External Services

**Key NuGet Packages (.NET API):**
- `Microsoft.EntityFrameworkCore.Sqlite/.SqlServer/.Postgres/.MySql` - Multi-database ORM
- `Microsoft.AspNetCore.SignalR` - Real-time communication
- `Refit.HttpClientFactory` - HTTP API clients
- `FluentValidation.AspNetCore` - Input validation
- `xunit`, `FluentAssertions`, `Microsoft.AspNetCore.Mvc.Testing` - Testing

**Key npm Packages (React Frontend):**
- `react` & `react-dom` - React framework
- `@microsoft/signalr` - Real-time SignalR client
- `@tanstack/react-query` - Server state management
- `axios` - HTTP client for API communication
- `react-router-dom` - Client-side routing
- `tailwindcss` - Utility-first CSS framework
- `react-hook-form` & `zod` - Form handling and validation
- `vite` - Build tool and dev server
- `vitest` & `@testing-library/react` - Testing framework

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
- **API Integration tests** in `src/tests/Farm.Web.Api.Tests/`
- **React component tests** in `src/Web/ReactApp/src/test/`
- Uses `CustomWebApplicationFactory` for API testing
- Uses Vitest and React Testing Library for frontend testing
- Tests API endpoints, database operations, and health checks
- Tests run against temporary SQLite database (in-memory)
- Total: 62 API tests covering core functionality (verified working)

**Manual Verification:**
1. API server starts successfully at http://localhost:5245 (Development profile)
2. React client starts successfully at http://localhost:3000 (Vite dev server)
3. Health check endpoints respond:
   - http://localhost:5245/health (comprehensive)
   - http://localhost:5245/healthz (basic)
4. API endpoints accessible (e.g., http://localhost:5245/api/printers)
5. React client serves modern TypeScript application with PrintFarmer title
6. Database initializes automatically (creates `farm.db` file)
7. Application seeds default manufacturers and printer models on first run
8. SignalR hub available at http://localhost:5245/hubs/printers
9. React client connects to SignalR for real-time updates

## Development Guidelines

**Code Style:**
- **C# (.NET API)**: PascalCase for types/members, camelCase for locals/parameters
- **TypeScript (React)**: camelCase for variables/functions, PascalCase for components/types
- Follow conventional .NET patterns for API code
- Follow React and TypeScript best practices for frontend code
- Run `dotnet format` for .NET code and `npm run lint` for React code before committing

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
- **API Backend (src/api/)**: Controllers, Services, Hubs, Data models
- **React Frontend (src/Web/ReactApp/)**: Components, pages, contexts, services, types
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
These instructions have been thoroughly tested and validated with .NET 9.0.302. Only search for additional information if these instructions are incomplete or you encounter errors not covered here. The build process, test execution, and development workflow have all been verified to work correctly.

## Critical Timeout Settings & Build Times

**NEVER CANCEL builds or long-running operations. Always set appropriate timeouts:**

| Command | Typical Time | Minimum Timeout | Notes |
|---------|--------------|-----------------|-------|
| `dotnet restore ./farm-web.sln` | ~41 seconds | 120 seconds | First run downloads packages |
| `npm install` (React dependencies) | ~30-60 seconds | 120 seconds | Downloads React packages |
| `dotnet build ./farm-web.sln -c Debug` | ~83 seconds | 150 seconds | Includes compilation warnings |
| `npm run build` (React production build) | ~20-40 seconds | 90 seconds | Vite optimized build |
| `dotnet test ./farm-web.sln -c Debug` | ~11 seconds | 60 seconds | Runs 62 integration tests |
| `npm test` (React tests) | ~5-10 seconds | 30 seconds | Vitest component tests |
| `dotnet format ./farm-web.sln` | ~80 seconds | 150 seconds | Formats entire .NET solution |
| `npm run lint` (React linting) | ~5-10 seconds | 30 seconds | ESLint checks |
| API server startup | ~15 seconds | 60 seconds | Database initialization |
| React dev server startup | ~5-10 seconds | 30 seconds | Vite development server |

**CRITICAL WARNINGS:**
- **NEVER CANCEL** commands that appear to hang - they are processing
- Build warnings are normal - build will still succeed
- Database warnings on first run are expected
- Set bash timeouts to at least 50% longer than typical times shown above

## Complete Working Example

**Full development workflow from fresh clone:**

```bash
# 1. Ensure .NET 9.0.302 is installed
dotnet --info  # Should show 9.0.302

# 2. Ensure Node.js 18+ is installed
node --version  # Should be 18+
npm --version

# 3. Navigate to working directory
cd ./src

# 4. Restore .NET dependencies (41 seconds, set timeout 120+)
dotnet restore ./farm-web.sln

# 5. Install React dependencies (30-60 seconds, set timeout 120+)
cd ./Web/ReactApp
npm install
cd ../../

# 6. Build .NET solution (83 seconds, set timeout 150+)
dotnet build ./farm-web.sln -c Debug

# 7. Build React application (20-40 seconds, set timeout 90+)
cd ./Web/ReactApp
npm run build
cd ../../

# 8. Run .NET tests (11 seconds, set timeout 60+)
dotnet test ./farm-web.sln -c Debug

# 9. Run React tests (fast, set timeout 30+)
cd ./Web/ReactApp
npm test
cd ../../

# 10. Format code (80+ seconds, set timeout 150+)
dotnet format ./farm-web.sln
cd ./Web/ReactApp && npm run lint && cd ../../

# 11. Start API server (Terminal 1)
dotnet run --project ./api/Farm.Web.Api.csproj
# Wait for: "Now listening on: http://localhost:5245"

# 12. Start React client (Terminal 2)
cd ./Web/ReactApp
npm run dev
# Wait for: "Local: http://localhost:3000/"

# 13. Validate everything works
curl -s http://localhost:5245/healthz        # Should return: {"status":"ok"}
curl -s http://localhost:5245/api/printers   # Should return: []
curl -s http://localhost:3000/ | head -5     # Should show HTML with PrintFarmer
curl -s http://localhost:5245/api/catalog/manufacturers | jq length  # Should return: 8
```

**Expected total time for fresh setup:** ~4-5 minutes (excluding .NET SDK and Node.js installation)
