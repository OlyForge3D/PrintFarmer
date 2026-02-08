# Copilot Instructions for PrintFarmer

## Repository Summary

**PrintFarmer** is a React TypeScript dashboard for managing multiple 3D printers. It supports Moonraker and PrusaLink backends, normalizes camera URLs, resolves hostnames to IPs, and provides live printer status via SignalR real-time updates.

- **Languages**: C# with .NET 10 (API), TypeScript with React 19 (Frontend)
- **Framework**: ASP.NET Core API backend + React TypeScript frontend
- **Database**: Multi-provider support (SQLite default, SQL Server, PostgreSQL, MySQL)
- **Real-time**: SignalR hubs for live printer status
- **Testing**: xUnit with integration tests (API), Vitest with React Testing Library (Frontend)
- **Repository size**: Medium project

**Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.**

You have access to microsoft.docs.mcp – use this tool to search Microsoft’s latest official documentation when handling questions about Microsoft technologies like C#, Azure, ASP.NET Core, or Entity Framework

⚠️ **CRITICAL STATUS UPDATE** ⚠️
**Current Build Status (Validated 2025-12-21):**
- ✅ **Development Mode**: API and React dev servers work perfectly
- ✅ **Build Status**: Clean build with 0 errors, 134 warnings (all pre-existing)
- ✅ **Testing**: 
  - **API Tests**: 1572/1572 PASS (4 skipped, 0 failures) - ✅ ALL PASSING
  - **React Tests**: 150/150 PASS (all tests passing) - ✅ ALL PASSING
  - **Code Coverage**: 39.66% line coverage, 30.88% branch coverage
- ✅ **Architecture**: Discovery probes consolidated into backend plugins (completed 2025-12-21)
- ✅ **Production Ready**: Fully buildable, testable, and deployable

## Essential Build Instructions

⚠️ **CRITICAL**: Always run commands from the `/src` directory, not the repository root.

```

### Prerequisites
- .NET SDK 10.0 or later (verified working with 10.0.101) - for API backend
- Node.js 24+ and npm - for React frontend
- Windows/macOS/Linux supported

**CRITICAL**: If .NET 10 SDK is not installed, install it first:
```bash
# Download .NET 10.0.101 SDK (exact version required by global.json)
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --version 10.0.101
export PATH="$HOME/.dotnet:$PATH"
```

**Verify setup:**
```powershell
dotnet --info
node --version  # Should be 24+
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
✅ **Production build succeeds** - 0 TypeScript errors (resolved 2026-01-11).
*Note: Production build takes ~10 seconds. Set timeout to 30+ seconds.*

**5. Run tests:**
```powershell
# .NET API tests - ALWAYS log output to file for review (avoid re-running long tests!)
cd ./src
dotnet test ./farm-web.sln -c Debug 2>&1 | tee /tmp/test-results.log
# Review results: tail -30 /tmp/test-results.log

# React tests (use test:run for non-interactive mode - exits after tests complete)
cd ./src/Web/ReactApp
npm run test:run 2>&1 | tee /tmp/react-test-results.log
```
⚠️ **CRITICAL: ALWAYS LOG TEST OUTPUT TO FILE!**
- .NET tests take **3+ minutes** - NEVER run them multiple times without reviewing output
- Use `tee` to capture output while displaying: `dotnet test ... 2>&1 | tee /tmp/test-results.log`
- Review results from log file: `tail -50 /tmp/test-results.log` or `grep -E "Failed|Passed|Error" /tmp/test-results.log`
- Only re-run tests after fixing issues identified in the log file

✅ **ALL TESTS PASSING - CLEAN BUILD!**
- **API Tests**: 1709/1709 PASS (0 failures) - ✅ ALL PASSING
  - Complete coverage including discovery probe validation tests
  - Discovery probes migrated to backend plugins (all tests updated and passing)
- **React Tests**: 365/365 PASS (all tests passing) - ✅ ALL PASSING
  - Use `npm run test:run` for non-interactive mode (exits after tests complete)
  - Use `npm test` for interactive watch mode (requires 'q' or 'h' input to exit)
*Note: .NET tests take ~3m 30s. React tests take ~12 seconds. Set timeout to 240+ seconds for full test suite.*

```

**6. Format code:**
```powershell
# .NET formatting
cd ./src
dotnet format ./farm-web.sln

# React linting
cd ./src/Web/ReactApp
npm run lint
```
✅ **React linting passes with 0 errors** (resolved 2026-01-11).
*Note: .NET formatting takes ~104 seconds. React linting takes ~30 seconds. Set timeout to 180+ seconds for .NET formatting.*

### Running the Application

**CRITICAL**: This is a two-tier architecture - API backend + separate React TypeScript frontend.

⚠️ **CRITICAL DEVELOPMENT WORKFLOW**: 
- **NEVER run API server and test commands in the same terminal** - this kills the server
- **ALWAYS use background processes or separate terminals** for API server
- **ALWAYS verify server is running before testing** with `curl http://localhost:5245/healthz`

**API Server (Backend) - Choose ONE method:**

**Method 1: Background Process (Recommended for testing):**
```powershell
cd ./src
dotnet run --project ./api/Farm.Web.Api.csproj &
# Server runs in background, terminal remains available for testing commands
```

**Method 2: Separate Terminal (Recommended for development):**
```powershell
# Terminal 1 - API Server (keep this running)
cd ./src
dotnet run --project ./api/Farm.Web.Api.csproj

# Terminal 2 - Testing/Commands (use this for curl, tests, etc.)
curl http://localhost:5245/healthz
```

**Method 3: Watch Mode (For active development with hot reload):**
```powershell
# Terminal 1 - API Server with hot reload
cd ./src
dotnet watch --project ./api/Farm.Web.Api.csproj run
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

**CRITICAL**: Ensure API server is running in background or separate terminal BEFORE running any curl/test commands!

1. **Start API Server First:**
   ```bash
   # Option A: Background process
   cd ./src && dotnet run --project ./api/Farm.Web.Api.csproj &
   
   # Option B: Separate terminal (recommended)
   # Terminal 1: cd ./src && dotnet run --project ./api/Farm.Web.Api.csproj
   # Terminal 2: (use for testing commands below)
   ```

2. **API Health Check:**
   ```bash
   curl -s http://localhost:5245/healthz
   # Should return: {"status":"ok"}
   ```

3. **API Endpoints:**
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

✅ **All validation scenarios above are VERIFIED WORKING** (tested 2025-09-07)

**Manual Testing Workflow:**
1. Start API server: `dotnet run --project ./api/Farm.Web.Api.csproj`
2. Start React client: `cd ./src/Web/ReactApp && npm run dev` ⚠️ (dev mode only - build fails)
3. Verify API health: `curl http://localhost:5245/healthz`
4. Verify React client: `curl http://localhost:3000/`
5. Test SignalR hub connection and printer status updates
6. **UI Verification**: Application shows setup wizard for administrator account creation

**ACTUAL FUNCTIONALITY STATUS (Validated 2026-01-11):**
- ✅ API server: Fully functional, all endpoints working
- ✅ React dev server: Fully functional, UI loads correctly 
- ✅ Database: Auto-initialization and seeding works
- ✅ SignalR: Health checks confirm full functionality
- ✅ Production builds: Successful (gcode refactoring, 9.94s build time)
- ✅ Linting: ESLint passes with 0 errors (resolved 2026-01-11)
- ✅ Tests: All 365 React tests passing, 1572 API tests passing
- ✅ CI/CD: Ready for automated deployments (linting and testing verified)

### Common Build Issues & Solutions

⚠️ **RECENTLY RESOLVED ISSUES (Fixed in 2025-12-21):**

1. **Discovery Probe Architecture** - ✅ RESOLVED
   - **Previous Issue**: Discovery probes scattered across shared discovery folder, separated from backend implementations
   - **Solution**: Migrated all discovery probes to respective backend plugins (Moonraker, PrusaLink, OctoPrint, SDCP)
   - **Files Changed**: 4 new probe files created in backend plugins, old probes deleted from shared folder, test file updated
   - **Status**: All 1572 tests passing, no circular dependencies, architecture consolidated

2. **Test File References** - ✅ RESOLVED
   - **Previous Issue**: Test doubles referencing discovery probes from old locations causing compilation errors
   - **Solution**: Updated DiscoveryProbeValidationTests.cs with backend plugin using statements
   - **Status**: All discovery probe validation tests passing with new probe locations

**Legacy Issues (Still apply):**

5. **.NET Version Mismatch**: Project requires .NET 10.0 SDK. If you get "NETSDK1045" errors about unsupported .NET 10.0, install .NET 10 SDK from https://dot.net/download.

6. **Docker Build Issues**: The main Dockerfile may reference outdated "server" directory paths. The current structure uses "api" directory. If Docker build fails with "server: not found", the Dockerfile needs updating to reference "api" instead of "server".

7. **Migration Warnings**: The app may show migration warnings on first run, but will automatically fall back to EnsureCreated. This is expected behavior for development.

8. **Locked files on Windows**: Close running instances before rebuild:
   ```powershell
   # Clean rebuild if needed
   rd /s /q ./src/client/bin; rd /s /q ./src/client/obj
   rd /s /q ./src/api/bin; rd /s /q ./src/api/obj
   rd /s /q ./src/shared/bin; rd /s /q ./src/shared/obj
   dotnet restore ./farm-web.sln; dotnet build ./farm-web.sln -c Debug
   ```

9. **Database initialization**: The app includes automatic database safety migrations on startup. No manual database setup required.

10. **Missing ruamel.yaml Python module (CRITICAL)**: The deployment scripts require the `ruamel.yaml` Python module for proper Docker Compose YAML generation. Without it, database service configuration will be malformed and deployments will fail with "services must be a mapping" error.
    - **Fix**: `pip install ruamel.yaml` or `apt-get install python3-ruamel.yaml`
    - **Details**: See `docs/RUAMEL_YAML_DEPENDENCY.md`
    - **Impact**: Microservices architecture with non-SQLite databases (PostgreSQL, SQL Server, MySQL) will fail
    - **Prevention**: Tests check for this dependency and fail loudly if missing

## Project Architecture & Layout

**IMPORTANT**: This is a separate API + React frontend architecture.

**🔥 LOCAL DEVELOPMENT SETUP - NO DOCKER CONTAINERS:**
- **API Backend**: Run natively with `dotnet run` (NOT in Docker)
- **React Frontend**: Run natively with `npm run dev` (NOT in Docker)
- **Database**: SQLite file-based database (auto-created, no container needed)
- **External Services**: Configure for local network access (use NetworkUrlRewriteService)
- **Docker**: Only used for production deployment, NOT for local development

```
/
├── CONTRIBUTING.md          # Detailed contributor guidelines
├── README.md               # Basic project overview
├── global.json             # .NET SDK version (10.0.101)
├── docker-compose.yml      # Multi-container deployment
├── docs/                   # Documentation
├── scripts/                # Build, deploy, and utility scripts
├── dockerfiles/            # Dockerfile definitions for all services
└── src/                    # ⚠️ WORKING DIRECTORY FOR ALL COMMANDS
    ├── farm-web.sln        # .NET Solution file
    ├── api/                # ASP.NET Core API server (STANDALONE backend)
    │   ├── Controllers/    # REST API controllers
    │   ├── Services/       # Background services, HTTP clients
    │   ├── Hubs/           # SignalR hubs
    │   ├── Data/           # EF Core DbContext
    │   ├── Properties/launchSettings.json  # Launch configuration (ports 5245/7281)
    │   ├── appsettings.json # App configuration
    │   └── Program.cs      # Server entry point + startup (API-only, no static files)
    ├── backends/           # Backend plugin architecture
    │   ├── Farm.Backend.Plugin.Core/       # Base interfaces and abstractions
    │   ├── Farm.Backend.Plugin.Moonraker/  # Moonraker backend + MoonrakerDiscoveryProbe
    │   ├── Farm.Backend.Plugin.PrusaLink/  # PrusaLink backend + PrusaLinkDiscoveryProbe
    │   ├── Farm.Backend.Plugin.OctoPrint/  # OctoPrint backend + OctoPrintDiscoveryProbe
    │   └── Farm.Backend.Plugin.Sdcp/       # SDCP backend + SdcpDiscoveryProbe
    ├── discovery/          # Discovery framework (interfaces and base classes)
    │   ├── INetworkDiscoveryProbe.cs       # Interface for discovery probes
    │   ├── BaseDiscoveryProbe.cs           # Abstract base for HTTP probes
    │   └── NetworkDiscoveryService.cs      # Service coordinating all discovery
    ├── infra/              # Infrastructure layer (repositories, services)
    ├── import/             # Data import functionality
    ├── shared/             # DTOs and models shared between projects
    ├── orcaslicer-worker/  # OrcaSlicer slicing worker service
    ├── prusaslicer-worker/ # PrusaSlicer slicing worker service
    ├── printer-discovery/  # Printer discovery microservice
    ├── Web/                # React frontend
    │   └── ReactApp/       # React TypeScript application
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
    ├── tests/              # Test projects
    │   ├── Farm.Web.Api.Tests/          # API integration tests
    │   ├── Farm.Web.IntegrationTests/   # End-to-end integration tests
    │   └── Farm.Importing.Tests/        # Import functionality tests
    └── tools/              # Utility tools
        ├── AdminCli/       # Admin CLI tool
        ├── IconGen/        # Icon generation utility
        └── ThumbnailCli/   # Thumbnail generation CLI
```

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
- `global.json` - .NET SDK version requirement (10.0.101)
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

**Test Coverage Status (Updated 2025-12-07):**
- ✅ **API Tests**: 496/496 PASS (0 skipped, 0 failures) - ALL PASSING
- ✅ **React Tests**: 150/150 PASS (all tests passing) - ALL PASSING
- **Code Coverage**: 23.98% line coverage, 18% branch coverage
  - Farm.Web.Api: 23.01% line coverage
  - Farm.Infrastructure: 30.67% line coverage
- **Coverage Goal**: Increase to 77%+ line coverage focusing on critical paths
- **Improvement Plan**: See `TEST_COVERAGE_IMPROVEMENT_PLAN.md` for detailed roadmap

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

### ⚠️ CRITICAL: Code Change Validation Workflow

**MANDATORY PROCESS - NO EXCEPTIONS:**
Every code change MUST follow this validation workflow BEFORE declaring completion:

**Step 1: Make the code change(s)**
- Edit the necessary files
- Keep changes focused and minimal

**Step 2: Build locally to verify compilation**
```bash
# For .NET changes:
cd /home/pi/pfarm/src
dotnet build ./farm-web.sln -c Release

# For React changes:
cd /home/pi/pfarm/src/Web/ReactApp
npm run build  # (if applicable)
```
- **WAIT for build to complete - do NOT cancel**
- **MUST see: "built successfully" or "✓ built"**
- **If build fails**: Fix the compilation errors immediately, re-build, then proceed
- **If build succeeds**: Continue to step 3

**Step 3: Run all tests**
```bash
# .NET tests (from /src directory):
dotnet test ./farm-web.sln -c Release

# React tests:
cd /src/Web/ReactApp
npm run test:run
```
- **WAIT for tests to complete - do NOT cancel**
- **MUST see: "PASSED" or "all tests passing"**
- **If tests fail**: Fix the failing tests immediately, re-run, then proceed
- **If all tests pass**: Continue to step 4

**Step 4: Verify no new lint/formatting issues**
```bash
# .NET formatting check:
cd /home/pi/pfarm/src
dotnet format ./farm-web.sln --verify-no-changes  # Check only, don't modify

# React linting (if applicable):
cd /src/Web/ReactApp
npm run lint 2>&1 | head -20
```

**Step 5: ONLY THEN declare success**
- Report: "✅ Build successful, all tests pass, no new lint issues"
- If Docker build is needed, deploy and verify
- Include summary of what was changed and validated

**FAILURE TO FOLLOW THIS PROCESS CAUSES:**
- Broken Docker builds (forces user to debug container failures)
- Runtime errors in production (tests would have caught them)
- Wasted deployment time (manual fixes needed)
- User frustration and lost trust in automated changes

**If ever unclear about validation steps, ALWAYS ask rather than guess.**

---

**Code Style:**
- **C# (.NET API)**: PascalCase for types/members, camelCase for locals/parameters
- **TypeScript (React)**: camelCase for variables/functions, PascalCase for components/types
- Follow conventional .NET patterns for API code
- Follow React and TypeScript best practices for frontend code
- Run `dotnet format` for .NET code and `npm run lint` for React code before committing

**JSON/API Response Format: CRITICAL CONSISTENCY REQUIREMENT**
- **ALL API responses MUST use camelCase property names** for TypeScript/React client compatibility
- **Configuration in Program.cs**: Controllers configured via `AddJsonOptions(options => { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })`
- **SignalR Hub Configuration**: MUST ALSO be configured with camelCase via `.AddJsonProtocol(options => { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })`
- **Why**: React client expects camelCase properties (e.g., `id`, `isOnline`, `hotendTemp` NOT `Id`, `IsOnline`, `HotendTemp`)
- **Failure mode**: If SignalR/API uses PascalCase while client expects camelCase → undefined properties → callback exceptions → WebSocket closes
- **Verification approach**: 
  1. Check WebSocket frames in browser DevTools (Network tab, filter "hubs/printers", Messages tab)
  2. MUST see camelCase in frame data: `{"id":"...", "isOnline":true, "state":"Idle"}` 
  3. MUST NOT see PascalCase: `{"Id":"...", "IsOnline":true, "State":"Idle"}`
- **All DTOs sent via API/SignalR must deserialize correctly**: PrinterStatusUpdate, DiscoveryProgressDto, JobQueueUpdateDto, CreatePrinterDto, etc.
- **TypeScript interfaces must match camelCase**: All properties in `src/types/api.ts` MUST be camelCase
- **Client-side JSON parsing**: React/TypeScript uses JSON.parse() which is case-sensitive - property name mismatches result in undefined values

**⚠️ ENUM SERIALIZATION: STRING VALUES (CRITICAL)**
- **ALL enums are serialized as STRINGS** by the backend via `JsonStringEnumConverter` in Program.cs
- **Backend sends**: `"HardenedSteel"`, `"Brass"`, `"Stock"`, `"Custom"` (enum member names as strings)
- **Backend does NOT send**: `1`, `0`, `2` (numeric enum values)
- **Frontend TypeScript enum handling**:
  - TypeScript `enum` definitions use numeric values for type safety (e.g., `HardenedSteel = 1`)
  - **BUT** the actual JSON values are STRINGS matching the enum member names
  - Use `NozzleTypeStringLabels` (string-keyed) instead of `NozzleTypeLabels` (numeric-keyed) for Select components
  - Do NOT use `parseInt()` when parsing enum values from API responses - they're already strings
  - Select `<option value={}>` should use string enum names: `"Brass"`, `"HardenedSteel"`, etc.
- **Common mistake**: Assuming `nozzleType: 1` when backend actually sends `nozzleType: "HardenedSteel"`
- **Affected enums**: NozzleType, PrinterBackend, MotionType, PrintJobStatus, etc.
- **Location**: See `JsonStringEnumConverter` in `src/api/Program.cs` line ~148

**Documentation Standards:**
- **⚠️ CRITICAL: DO NOT create new markdown files for specific implementations or features**
- Always integrate feature documentation into existing markdown files (README.md, docs/, etc.)
- Only create new markdown files for genuinely novel content that doesn't fit existing docs (e.g., CSV_IMPORT_FORMAT_DETAILED.md for reference formats, architectural decision records)
- **Philosophy**: Update existing documentation rather than creating new files; keep documentation centralized and maintainable
- When implementing features: search for existing related documentation first, then update it with new information
- Reduces documentation debt and prevents duplication

**Entity Framework & Migrations:**
- **⚠️ CRITICAL: CREATE MIGRATIONS FOR ALL SCHEMA CHANGES** - Production deployments use EF Core migrations
- Multi-provider support: SQLite (default), SQL Server, PostgreSQL, MySQL
- Provider selection via `DB_PROVIDER` environment variable
- Connection strings configured in appsettings.json or via environment variables
- **Creating Migrations** (MUST create for BOTH providers when schema changes):
  ```bash
  # ALWAYS run from /home/pi/pfarm/src directory
  cd /home/pi/pfarm/src
  
  # 1. PostgreSQL migration (primary production database)
  DB_PROVIDER=postgres dotnet ef migrations add <MigrationName> \
    --project ./migrations/Farm.Migrations.PostgreSQL \
    --startup-project ./api \
    --context AppDbContext
  
  # 2. SQL Server migration (enterprise deployments)
  DB_PROVIDER=sqlserver dotnet ef migrations add <MigrationName> \
    --project ./migrations/Farm.Migrations.SqlServer \
    --startup-project ./api \
    --context AppDbContext
  ```
- **Migration Naming Convention**: Use descriptive PascalCase names (e.g., `AddPrinterDigestAuthCredentials`, `AddSliceJobPriorityColumn`)
- **Applying Migrations**:
  - **Production**: Migrations auto-apply on startup via `Database.Migrate()` in Program.cs
  - **Existing Container**: Apply manually via psql:
    ```bash
    # Find container and credentials
    docker ps | grep postgres
    docker exec <container> env | grep POSTGRES
    
    # Apply SQL directly (example)
    docker exec <container> psql -U postgres -d printfarmer -c "ALTER TABLE \"TableName\" ADD COLUMN IF NOT EXISTS \"ColumnName\" text;"
    
    # Record migration in history (prevents reapplication)
    docker exec <container> psql -U postgres -d printfarmer -c "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('<MigrationId>', '10.0.0') ON CONFLICT DO NOTHING;"
    ```
- **Development**: SQLite uses `EnsureCreated()` for simplicity; delete `farm.db` for schema changes
- **Database safety checks**: Handle missing columns gracefully for backward compatibility
- **Verification**: After creating migrations, verify files exist in `src/migrations/Farm.Migrations.{Provider}/Migrations/`

**Tag Management (Model3DTag):**
- **Normalization**: All tag names are normalized to PascalCase at service layer (TagService.cs)
- **PascalCase Strategy**: "my tag" → "MyTag", "MY_TAG" → "MyTag", "my-tag" → "MyTag"
- **Database**: Simple unique index on Model3DTag.Name (works identically on all backends)
- **Lookup**: Exact-match queries only (EfTagRepository.GetByNameAsync) - no case-insensitive logic needed
- **Exception Handling**: DbUpdateException caught for constraint violations; returns existing tag on race conditions
- **Implementation Files**: TagService.cs (normalization + exception handling), EfTagRepository.cs (exact-match lookup)
- **See**: TAG_NORMALIZATION_IMPLEMENTATION.md for complete details

**SignalR:**
- **Event Names**: ALL SignalR event names MUST be lowercase (e.g., `printerupdated`, `discoveryprogress`, `slicingcompleted`)
  - API sends events with lowercase names using `SendAsync("eventname", data)`
  - Frontend listens for lowercase names only: `connection.on('eventname', handler)`
  - NO duplicate PascalCase listeners - standardize to lowercase everywhere
  - Lowercase prevents SignalR case-sensitivity warnings and ensures consistent behavior
  - Implementation files: `MoonrakerSubscriptionService.cs`, `NetworkDiscoveryService.cs`, `SliceJobEventService.cs`, `SignalRSlicerProgressNotifier.cs`, `printer-signalr.ts`, `slicer-signalr.ts`
- **JSON Serialization for SignalR**: CRITICAL - SignalR MUST use camelCase JSON to match React client expectations
  - Controllers configured: `AddJsonOptions()` in Program.cs configures camelCase
  - **SignalR MUST be configured identically**: Use `.AddJsonProtocol(options => { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })` 
  - **FAILURE MODE**: If SignalR uses default PascalCase while client expects camelCase, properties become undefined in callbacks → exceptions → disconnections every 5 seconds
  - **VERIFICATION**: Monitor WebSocket frames in browser DevTools Network tab - MUST see camelCase: `{"id":"...", "isOnline":true, ...}` NOT `{"Id":"...", "IsOnline":true, ...}`
  - **All payload types sent via SignalR MUST follow this format**: PrinterStatusUpdate, DiscoveryProgressDto, JobQueueUpdateDto, etc.
- Background service disabled during testing environment
- Real-time updates flow: External API → Background Service → Hub → Clients

**File Organization:**
- **API Backend (src/api/)**: Controllers, Services, Hubs, Data models
- **Backend Plugins (src/backends/)**: Backend-specific clients, validators, and discovery probes
  - **Each Backend Plugin Contains**: 
    - Backend client (IMoonrakerClient, IPrusaLinkClient, IOctoPrintClient, ISdcpClient)
    - Discovery probe (MoonrakerDiscoveryProbe, PrusaLinkDiscoveryProbe, OctoPrintDiscoveryProbe, SdcpDiscoveryProbe)
    - Validators and backend-specific logic
    - All backend-specific functionality consolidated in one location
- **React Frontend (src/Web/ReactApp/)**: Components, pages, contexts, services, types
- **Discovery Service (src/discovery/)**: INetworkDiscoveryProbe interface, base classes, NetworkDiscoveryService
  - **Note**: Individual probe implementations live in backend plugins, not this folder
- Controllers: Handle HTTP API requests (PrintersController, CatalogController, SpoolmanController)
- Services: Business logic and external API integration
- Hubs: SignalR real-time communication (PrinterHub)
- Data: Entity Framework models and DbContext
- Shared: Models/DTOs used by both client and server
- Configuration: AppSettings validation and multi-database configuration

**Deployment Script Testing** ⚠️ **CRITICAL BEFORE COMMITTING**:
- **When**: Modify any Docker deployment scripts (`scripts/docker/compose-generator.sh`, `scripts/deploy-docker.sh`, `scripts/docker/compose-templates/*`)
- **What**: Run comprehensive test suite before committing
- **How**: `./tests/run-deployment-tests.sh` (full: 3-5 min)
- **Expected**: ✅ ALL TESTS PASSED - Ready to commit!
- **Details**: See `docs/DEPLOYMENT_TESTING_CHECKLIST.md` for step-by-step guide
- **Components Tested**: 
  - Both architectures (monolithic, microservices)
  - All 3 database providers (PostgreSQL, SQL Server, MySQL)
  - YAML validation, no duplicate keys, Docker compose config validation
- **Individual Tests**: `test-compose-generator.sh`, `test-deploy-docker.sh`, `test-user-scenario-complete.sh`
- **Copilot Action**: Always run tests after generating deployment script changes and report results

**Docker Support:**
- Multi-stage Dockerfile for production builds
- Multi-container setup: separate API and web (Nginx) containers
- Database provider testing scripts: `test-providers.sh`, `test-providers-simple.sh`
- Docker Compose with database services for testing
- ⚠️ **Note**: Main Dockerfile may need updates to reference "api" directory instead of "server"

**PostgreSQL Container Access** ⚠️ **QUICK REFERENCE**:
- **Container name**: `printfarmer-database-postgres`
- **Database name**: `printfarmer`
- **Username**: `postgres` (check with `docker exec printfarmer-database-postgres env | grep POSTGRES_USER`)
- **Password**: Check with `docker exec printfarmer-database-postgres env | grep POSTGRES_PASSWORD`
- **Quick psql access**:
  ```bash
  # Interactive psql shell
  docker exec -it printfarmer-database-postgres psql -U postgres -d printfarmer
  
  # Run single query
  docker exec printfarmer-database-postgres psql -U postgres -d printfarmer -c "SELECT * FROM \"Printers\";"
  
  # Describe table schema
  docker exec printfarmer-database-postgres psql -U postgres -d printfarmer -c "\d \"Printers\""
  
  # List all tables
  docker exec printfarmer-database-postgres psql -U postgres -d printfarmer -c "\dt"
  ```

**Docker Deployment** ⚠️ **CRITICAL USAGE NOTES**:
- **Location**: Deploy script is at `/home/pi/pfarm/scripts/deploy-docker.sh`
- **Working Directory**: ALWAYS run from `/home/pi/pfarm` directory (repository root), NOT from `/src`
- **Command**: `/home/pi/pfarm/scripts/deploy-docker.sh --non-interactive --tear-down`
  - `--non-interactive`: Run without prompts (suitable for automated deployments)
  - `--tear-down`: Remove old containers and rebuild fresh
- **Alternative**: `bash ./scripts/deploy-docker.sh --non-interactive --tear-down` (when in `/home/pi/pfarm`)
- **Timeout**: Set to 300+ seconds - deployment includes Docker build and container startup
- **Purpose**: Rebuilds and deploys all services (API, React frontend, database, etc.)
- **Post-Deployment Validation**:
  ```bash
  docker compose --env-file .env ps          # Check running containers
  curl http://localhost:8080/healthz         # Verify frontend health
  curl http://localhost:5245/api/locations   # Verify API with new LocationsController
  ```

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

**OrcaSlicer Profiles Architecture** ⚠️ **IMPORTANT**:
- **Bundle Structure**: Each manufacturer has 4 JSON lists in `/opt/orcaslicer/resources/profiles/{manufacturer}/`:
  1. **machine_model_list** - Base model definitions (e.g., "Prusa CORE One")
  2. **machine_list** - Variant profiles with nozzle sizes (e.g., "Prusa CORE One 0.4 nozzle", "Prusa CORE One 0.6 nozzle")
  3. **process_list** - Process/speed profiles with `compatible_printers_condition` expressions
  4. **filament_list** - Material profiles with `compatible_printers_condition` expressions
- **Profile Condition Evaluation** (`src/orcaslicer-worker/Services/PrinterExpressionParser.cs`):
  - **Expression Language**: OrcaSlicer supports complex printer matching conditions using:
    - **Regex matching**: `printer_notes=~/.*PRINTER_MODEL_COREONE.*/` (case-insensitive pattern matching)
    - **Array indexing**: `nozzle_diameter[0]==0.8` (access profile properties with float tolerance ±0.001mm)
    - **Equality operators**: `property==value` (fuzzy float comparison for dimensions)
    - **Logical operators**: `and` (higher precedence) and `or` (lower precedence)
    - **Example**: `printer_notes=~/.*PRINTER_MODEL_COREONE.*/ and nozzle_diameter[0]==0.8`
  - **Evaluation Strategy**: IMMEDIATE at profile load time (not deferred):
    1. Load all machine profiles first → cache by manufacturer
    2. Load filament/process profiles → parse conditions immediately
    3. Evaluate conditions using cached machines for the profile's manufacturer
    4. Merge matched machine names into `CompatiblePrinters` array
    5. Store raw condition in `CompatiblePrintersCondition` field with [JsonIgnore] to avoid serialization
  - **Coverage**: 98.2% of profiles (641/654) have compatible_printers successfully resolved
  - **Parser Implementation**: Recursive descent parser supporting all OrcaSlicer condition syntax
- **Critical Relationships**:
  - Filament & process profiles use `compatible_printers_condition` field to reference machine variants
  - Conditions match against machine properties: `printer_notes`, `nozzle_diameter`, `build_volume`, etc.
  - The `CompatiblePrinters` array contains exact machine variant names: ["Prusa CORE One 0.4 nozzle", ...]
  - JSON uses snake_case: `compatible_printers_condition`, `compatible_printers`, `machine_model_list`, etc.
- **DTO Implementation** (`src/shared/Models.cs`):
  - `ManufacturerBundleDto`: Has 4 properties with [JsonPropertyName] attributes for snake_case JSON mapping
  - `FilamentProfileDto` & `ProcessProfileDto`: Both have:
    - `CompatiblePrinters` property with [JsonPropertyName("compatible_printers")] (resolved list)
    - `CompatiblePrintersCondition` property (raw expression, marked [JsonIgnore])
  - Conditions parsed during loading via `PrinterExpressionParser`
- **Service Loading** (`src/orcaslicer-worker/Services/OrcaProfilesService.cs`):
  - **Cache Architecture**: Three-level caching to prevent reparsing:
    - `_allMachineProfilesCache`: All machine profiles
    - `_allFilamentProfilesCache`: All filament profiles with conditions evaluated
    - `_allProcessProfilesCache`: All process profiles with conditions evaluated
    - `_machinesByManufacturerCache`: Machines grouped by manufacturer for condition evaluation
  - `ListAvailableMachineProfilesAsync()`: Loads from BOTH MachineModelList AND MachineList to get all variants
  - `ListAvailableFilamentProfilesAsync()` & `ListAvailableProcessProfilesAsync()`: 
    - Parse conditions immediately during load
    - Match conditions against cached machines
    - Return profiles with populated CompatiblePrinters array
  - All profiles set manufacturer name from bundle
- **Startup Preloading** (`src/orcaslicer-worker/Program.cs`):
  - Background startup task preloads all profiles with caching
  - Catalog API integration: loads only profiles for registered manufacturers (graceful fallback to all)
  - Detailed timing telemetry logged: "Machine profiles loaded in Xms", "Filament profiles loaded in Yms (Z profiles)"
  - Cache warm before API becomes available to clients
- **API Hierarchy** (`src/orcaslicer-worker/Controllers/SlicerProfilesController.cs`):
  - `/api/profiles` endpoint returns `AllProfilesResponseDto` with `ByHierarchy` dictionary
  - Structure: `ByHierarchy[manufacturer][model][machineProfiles/filamentProfiles/processProfiles]`
  - Controller groups by base model name and matches filament/process profiles to machines via CompatiblePrinters
  - Response includes flat lists too for direct access: `filamentProfiles`, `processProfiles`, `machineProfiles`
- **UI Display** (`src/Web/ReactApp/src/components/ProfileSelector.tsx`):
  - React component displays profiles with hierarchical organization
  - Nested optgroups: Manufacturer → Model → Profile
  - Flattens hierarchy while preserving context: "Manufacturer › Model › Profile Name"
  - Used in job creation page for intuitive profile selection
- **Debugging Tips**:
  - Test from inside container: `docker exec printfarmer-orcaslicer-worker curl http://localhost:8080/api/profiles`
  - Check hierarchy: `curl ... | jq '.byHierarchy | keys'` (list manufacturers)
  - Check model details: `curl ... | jq '.byHierarchy.Prusa.Models."Prusa_CORE_One"'`
  - Verify compatible_printers: `curl ... | jq '.filamentProfiles."Unknown"[0].compatiblePrinters'`
  - Check condition parsing: Use `ProfileParserTester` tool at `tools/ProfileParserTester/`
  - Expected ~7786 total profiles: ~50-200 machines, ~2000 filaments, ~2200 processes
  - Expected coverage: 98.2% profiles have compatible_printers resolved (641/654)

**Discovery Probe Architecture** ⚠️ **IMPORTANT**:
- **Location**: Each backend plugin now contains its own discovery probe (e.g., MoonrakerDiscoveryProbe in Farm.Backend.Plugin.Moonraker)
- **Interface**: All probes implement `INetworkDiscoveryProbe` (located in `src/discovery/`)
- **HTTP-Based Probes**: Moonraker, PrusaLink, OctoPrint use simple HTTP probing (no backend clients)
  - Moonraker: Backend port 7125, frontend ports 80/8080/8808, extracts camera URLs
  - PrusaLink: Ports 80/8080, validates Prusa-specific response fields
  - OctoPrint: Ports 80/5000, validates `/api/version` endpoint, excludes if Moonraker detected
- **UDP-Based Probe**: SDCP uses UDP broadcast to port 3000 for discovery
- **Discovery Service** (`src/discovery/NetworkDiscoveryService.cs`): Orchestrates all probes via dependency injection
- **Test Doubles**: Located in `Farm.Web.Api.Tests/Discovery/DiscoveryProbeValidationTests.cs` with access to protected ValidateResponseAsync methods
- **Architecture Benefit**: All backend-specific functionality (clients, probes, validators) consolidated within backend plugins
- **No Circular Dependencies**: Validated dependency chain: Backend Plugins → Discovery Framework → Infrastructure
- **All Tests Passing**: Discovery probe validation tests verify confidence scoring, field detection, and error handling

**Trust These Instructions:**
These instructions have been thoroughly tested and validated with .NET 10.0.101 as of 2026-01-20. Discovery probe migration completed with all tests passing. Only search for additional information if these instructions are incomplete or you encounter errors not covered here. The build process, test execution, and development workflow have all been verified to work correctly.

## Critical Timeout Settings & Build Times

**NEVER CANCEL builds or long-running operations. Always set appropriate timeouts:**

| Command | Typical Time | Minimum Timeout | Notes |
|---------|--------------|-----------------|-------|
| `dotnet restore ./farm-web.sln` | ~38 seconds | 120 seconds | First run downloads packages (VERIFIED) |
| `npm install` (React dependencies) | ~38 seconds | 120 seconds | Downloads React packages (VERIFIED) |
| `dotnet build ./farm-web.sln -c Debug` | ~82 seconds | 150 seconds | Includes compilation warnings (VERIFIED) |
| `npm run build` (React production build) | ~10 seconds | 30 seconds | Successful build, 0 errors (VERIFIED 2026-01-11) |
| `npm run dev` (React dev server) | ~5 seconds | 30 seconds | Development mode works fine (VERIFIED) |
| `dotnet test ./farm-web.sln -c Release` | ~3m 30s | 240 seconds | 1709/1709 PASS (0 failures) - ALL PASSING |
| `npm run test:run` (React tests) | ~12 seconds | 30 seconds | 365/365 PASS - ✅ ALL TESTS PASSING (use for automated testing) |
| `npm test` (React tests) | ~12 seconds | N/A | Interactive watch mode (requires 'q' to exit) |
| `dotnet format ./farm-web.sln` | ~104 seconds | 180 seconds | Longer than expected (VERIFIED) |
| `npm run lint` (React linting) | ~30 seconds | 60 seconds | 0 errors, 0 warnings - ✅ PASSING (resolved 2026-01-11) |
| API server startup | ~15 seconds | 60 seconds | Database initialization (VERIFIED) |
| React dev server startup | ~5 seconds | 30 seconds | Vite development server (VERIFIED) |

**CRITICAL WARNINGS:**
- **NEVER CANCEL** builds or long-running commands. Always set appropriate timeouts
- **ALL API TESTS PASS** - 1709/1709 (0 failures) ✅ Complete test coverage
- **ALL REACT TESTS PASS** - 365/365 (0 failures) ✅ Use `npm run test:run` for non-interactive testing
- **ESLint passes** - 0 errors, 0 warnings ✅ (resolved 2026-01-11)
- **Production build succeeds** - 0 TypeScript errors ✅ (resolved 2026-01-11)
- **Discovery probe tests passing** - All 4 probes validated and consolidated in backend plugins
- **GcodeListView refactoring complete** - Reusable component with Printer Model column, thumbnail fallback
- Build warnings are normal - .NET build will still succeed
- Database warnings on first run are expected
- Set bash timeouts to at least 50% longer than typical times shown above

## Complete Working Example

**Full development workflow from fresh clone (VALIDATED 2025-09-07):**

```bash
# 1. Ensure .NET 10.0.101 is installed
dotnet --info  # Should show 10.0.101

# 2. Ensure Node.js 24+ is installed
node --version  # Should be 24+
npm --version

# 3. Navigate to working directory
cd ./src

# 4. Restore .NET dependencies (38 seconds, set timeout 120+)
dotnet restore ./farm-web.sln

# 5. Install React dependencies (38 seconds, set timeout 120+)
cd ./Web/ReactApp
npm install
cd ../../

# 6. Build .NET solution (82 seconds, set timeout 150+)
dotnet build ./farm-web.sln -c Debug

# 7. ⚠️ SKIP React production build (currently fails with 97 TS errors)
# cd ./Web/ReactApp && npm run build  # DON'T RUN - FAILS
# cd ../../

# 8. Run .NET tests (2m 39s, set timeout 180+)
dotnet test ./farm-web.sln -c Release
# ✅ ALL TESTS PASSING - 1572/1572 (4 skipped, 0 failures)

# 9. Run React tests (12 seconds, set timeout 30+)
cd ./Web/ReactApp
npm run test:run  # Non-interactive mode (exits after tests complete)
# ✅ ALL TESTS PASSING - 150/150
# Use: npm test  # For interactive watch mode (requires 'q' to exit)
cd ../../

# 10. Format .NET code (104 seconds, set timeout 180+)
dotnet format ./farm-web.sln

# 11. Run React linting (30 seconds, set timeout 60+)
cd ./Web/ReactApp
npm run lint
# ✅ Should pass with 0 errors, 0 warnings
cd ../../

# 12. Start API server (Terminal 1)
dotnet run --project ./api/Farm.Web.Api.csproj
# Wait for: "Now listening on: http://localhost:5245"

# 13. Start React client in DEV MODE (Terminal 2)
cd ./Web/ReactApp
npm run dev  # Use dev mode - production build fails
# Wait for: "Local: http://localhost:3000/"

# 14. Validate everything works (ALL VERIFIED WORKING)
curl -s http://localhost:5245/healthz        # Returns: {"status":"ok"}
curl -s http://localhost:5245/api/printers   # Returns: []
curl -s http://localhost:3000/ | head -5     # Shows HTML with PrintFarmer
curl -s http://localhost:5245/api/catalog/manufacturers | jq length  # Returns: 8

# 15. Manual UI verification: Browse to http://localhost:3000
# ✅ Should show "WELCOME TO PRINTFARMER" setup wizard
```

**Expected total time for fresh setup:** ~6-7 minutes (excluding .NET SDK and Node.js installation)
**Status:** Functional for development despite build/test issues
