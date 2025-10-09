# Program.cs Refactoring Guide

## Problem
The current `Program.cs` is over 1,150 lines and mixes multiple concerns:
- Service registration (database, caching, HTTP clients, business services)
- Database initialization and seeding
- CLI command handling (--create-admin, --list-users)  
- Middleware configuration
- Endpoint routing
- OpenTelemetry setup
- Authentication/Authorization setup

## Solution
Extract concerns into focused extension methods in separate files:

### 1. ServiceCollectionExtensions.cs
**Location:** `src/api/Infrastructure/ServiceCollectionExtensions.cs`

**Purpose:** Centralize service registration

**Methods:**
- `AddPrintFarmerDatabase()` - Database and EF Core setup
- `AddPrintFarmerSettings()` - Settings service registration
- `AddPrintFarmerServices()` - All business services (caching, API clients, discovery, etc.)

### 2. DatabaseInitializationExtensions.cs
**Location:** `src/api/Infrastructure/DatabaseInitializationExtensions.cs`

**Purpose:** Handle database initialization in correct order

**Methods:**
- `InitializeDatabaseAsync()` - Ensures schema exists, then initializes and seeds

**Key Fix:** Calls `EnsureCreatedAsync()` BEFORE resolving `SettingsService`, preventing queries against missing tables.

### 3. CliCommandExtensions.cs
**Location:** `src/api/Infrastructure/CliCommandExtensions.cs`

**Purpose:** Handle headless CLI commands

**Methods:**
- `HandleCliCommandsAsync()` - Returns true if CLI command executed (app should exit)
- Private methods for --list-users and --create-admin

## Refactored Program.cs Structure

The new `Program.cs` should be ~400-500 lines instead of 1,150+:

```csharp
using Farm.Web.Api.Infrastructure;
// ... other necessary usings

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// === SERVICE REGISTRATION ===
builder.Services.AddPrintFarmerDatabase(builder.Configuration);
builder.Services.AddPrintFarmerSettings();
builder.Services.AddPrintFarmerServices();

// Controllers, Swagger, CORS, OpenTelemetry, Auth (keep as-is)
// ...

WebApplication app = builder.Build();

// === CLI COMMAND HANDLING ===
if (await app.HandleCliCommandsAsync(args))
{
    return; // CLI command executed, exit
}

// === MIDDLEWARE PIPELINE ===
// ... (keep existing middleware configuration)

// === DATABASE INITIALIZATION ===
await app.InitializeDatabaseAsync();

// === RUN APPLICATION ===
await app.RunAsync();
```

## Benefits

1. **Separation of Concerns** - Each file has a single responsibility
2. **Testability** - Extension methods can be unit tested independently  
3. **Readability** - Program.cs shows high-level flow at a glance
4. **Maintainability** - Changes to service registration don't clutter Program.cs
5. **Bug Fix** - Database initialization happens in correct order

## Migration Steps

1. ✅ Create `ServiceCollectionExtensions.cs`
2. ✅ Create `DatabaseInitializationExtensions.cs`  
3. ✅ Create `CliCommandExtensions.cs`
4. ⏳ Update `Program.cs` to use the new extensions
5. ⏳ Test that application starts correctly
6. ⏳ Test CLI commands still work

## Files Created

- `/src/api/Infrastructure/ServiceCollectionExtensions.cs` (90 lines)
- `/src/api/Infrastructure/DatabaseInitializationExtensions.cs` (67 lines)
- `/src/api/Infrastructure/CliCommandExtensions.cs` (113 lines)

Total: 270 lines of well-organized, focused code replacing ~500 lines of mixed concerns in Program.cs.
