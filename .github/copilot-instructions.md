# Copilot Instructions for ForgeIQ

## Repository Summary

**ForgeIQ** (also called "PrintFarmer") is a Blazor WebAssembly (hosted) dashboard for managing multiple 3D printers. It supports Moonraker and PrusaLink backends, normalizes camera URLs, resolves hostnames to IPs, and provides live printer status via SignalR real-time updates.

- **Language**: C# with .NET 8
- **Framework**: ASP.NET Core backend + Blazor WebAssembly frontend
- **Database**: SQLite with Entity Framework Core
- **Real-time**: SignalR hubs for live printer status
- **Testing**: xUnit with integration tests using WebApplicationFactory
- **Repository size**: ~50 source files, small-to-medium project

## Essential Build Instructions

⚠️ **CRITICAL**: Always run commands from the `/src` directory, not the repository root.

### Prerequisites
- .NET SDK 8.0 or later (verified working with 8.0.119)
- Windows/macOS/Linux supported

**Verify setup:**
```powershell
dotnet --info
```

### Bootstrap & Build Process

**1. Restore dependencies:**
```powershell
cd ./src
dotnet restore ./farm-web.sln
```

**2. Build solution:**
```powershell
# Debug build (default for development)
dotnet build ./farm-web.sln -c Debug

# Release build
dotnet build ./farm-web.sln -c Release
```

**3. Run tests:**
```powershell
dotnet test ./farm-web.sln -c Debug
```

**4. Format code:**
```powershell
dotnet format ./farm-web.sln
```

### Running the Application

**Development server (with hosted Blazor client):**
```powershell
cd ./src
dotnet run --project ./server/Farm.Web.Server.csproj
```
- Server starts at http://localhost:5088
- Swagger UI available at http://localhost:5088/swagger
- Health check at http://localhost:5088/healthz

**Hot reload for active development:**
```powershell
cd ./src
dotnet watch --project ./server/Farm.Web.Server.csproj run
```

### Common Build Issues & Solutions

1. **Case-sensitive path errors**: The solution file previously had incorrect casing (Server vs server). This has been fixed, but watch for similar issues.

2. **Blazor component TValue errors**: When using `<InputSelect>` with enums, always specify the TValue parameter:
   ```razor
   <InputSelect TValue="EnumType" @bind-value="model.Property">
   ```

3. **Locked files on Windows**: Close running instances before rebuild:
   ```powershell
   # Clean rebuild if needed
   rd /s /q ./src/client/bin; rd /s /q ./src/client/obj
   rd /s /q ./src/server/bin; rd /s /q ./src/server/obj
   rd /s /q ./src/shared/bin; rd /s /q ./src/shared/obj
   dotnet restore ./farm-web.sln; dotnet build ./farm-web.sln -c Debug
   ```

4. **Database initialization**: The app includes automatic SQLite safety migrations on startup. No manual database setup required.

## Project Architecture & Layout

```
/
├── CONTRIBUTING.md          # Detailed contributor guidelines
├── README.md               # Basic project overview
└── src/                    # ⚠️ WORKING DIRECTORY FOR ALL COMMANDS
    ├── farm-web.sln        # Solution file
    ├── client/             # Blazor WebAssembly client
    │   ├── Pages/          # Razor pages/components
    │   ├── Services/       # Client-side services
    │   ├── wwwroot/        # Static assets (CSS, JS, icons)
    │   └── Program.cs      # Client entry point
    ├── server/             # ASP.NET Core API server (hosts client)
    │   ├── Controllers/    # REST API controllers
    │   ├── Services/       # Background services, HTTP clients
    │   ├── Hubs/           # SignalR hubs
    │   ├── Data/           # EF Core DbContext
    │   ├── Migrations/     # EF Core migrations
    │   ├── Properties/launchSettings.json  # Launch configuration
    │   ├── appsettings.json # App configuration
    │   └── Program.cs      # Server entry point + startup
    ├── shared/             # DTOs and models shared between client/server
    ├── tests/              # Integration tests
    │   └── Farm.Web.Server.Tests/
    └── tools/IconGen/      # Utility tool for icon generation
```

### Key Architectural Components

**Server Architecture:**
- **Controllers**: REST API endpoints for printers, health checks
- **SignalR Hubs**: Real-time printer status updates (`PrinterHub`)
- **Background Services**: `MoonrakerSubscriptionService` for live updates
- **HTTP Clients**: Separate clients for Moonraker and PrusaLink APIs using Refit
- **Database**: SQLite with automatic schema safety checks and migrations

**Client Architecture:**
- **Blazor WebAssembly**: SPA hosted by the server
- **Pages**: Razor components for UI (Printers management, etc.)
- **SignalR Client**: Connects to server hubs for real-time updates

**Data Flow:**
1. Client UI → Server API → Database (CRUD operations)
2. Server Background Service → External Printer APIs → SignalR Hub → Client (real-time status)

### Configuration Files

- `src/server/appsettings.json` - Database connection, logging
- `src/server/Properties/launchSettings.json` - Development server settings
- `src/farm-web.sln` - Solution configuration
- Project files: `*.csproj` in each directory

### Dependencies & External Services

**Key NuGet Packages:**
- `Microsoft.EntityFrameworkCore.Sqlite` - Database ORM
- `Microsoft.AspNetCore.SignalR` - Real-time communication
- `Refit.HttpClientFactory` - HTTP API clients
- `Microsoft.AspNetCore.Components.WebAssembly.Server` - Blazor hosting
- `xunit`, `FluentAssertions`, `Microsoft.AspNetCore.Mvc.Testing` - Testing

**External APIs:**
- Moonraker API (Klipper 3D printer firmware)
- PrusaLink API (Prusa 3D printer firmware)

### Validation & Testing

**Pre-commit checks:**
1. Build succeeds: `dotnet build ./farm-web.sln -c Debug`
2. Tests pass: `dotnet test ./farm-web.sln -c Debug`
3. Code formatted: `dotnet format ./farm-web.sln`

**Test Structure:**
- Integration tests in `src/tests/Farm.Web.Server.Tests/`
- Uses `CustomWebApplicationFactory` for testing
- Tests API endpoints, database operations, and health checks
- Tests run against in-memory database

**Manual Verification:**
1. Server starts successfully and serves Swagger UI
2. Health check endpoint responds with `{"status":"ok"}`
3. Database initializes automatically (creates `farm.db` file)

## Development Guidelines

**Code Style:**
- C#: PascalCase for types/members, camelCase for locals/parameters
- Follow conventional .NET patterns
- Run `dotnet format` before committing

**Entity Framework:**
- Migrations are applied automatically on startup
- Database safety checks handle missing columns in SQLite
- No manual migration commands needed for typical development

**SignalR:**
- Background service disabled during testing environment
- Real-time updates flow: External API → Background Service → Hub → Clients

**File Organization:**
- Controllers: Handle HTTP API requests
- Services: Business logic and external API integration  
- Hubs: SignalR real-time communication
- Data: Entity Framework models and DbContext
- Shared: Models/DTOs used by both client and server

**Trust These Instructions:**
These instructions have been thoroughly tested and validated. Only search for additional information if these instructions are incomplete or you encounter errors not covered here. The build process, test execution, and development workflow have all been verified to work correctly.