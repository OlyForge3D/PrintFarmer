# Backend Client Migration to Plugins - Complete Plan

## Overview
This document outlines the comprehensive migration of ALL backend client code from `src/api/Services` to their respective plugin projects (`src/backends/Farm.Backend.Plugin.*`). The goal is to achieve zero compile-time dependencies from the API on specific backend implementations.

## Architecture

### NEW Pattern (Target)
```
API (Farm.Web.Api)
  ├─ Depends on: Infrastructure.Contracts (interfaces only)
  ├─ Depends on: Farm.SignalR (for PrinterHub)
  └─ Depends on: IBackendPluginRegistry (for dynamic plugin discovery)

Plugins (Farm.Backend.Plugin.*)
  ├─ Farm.Backend.Plugin.Moonraker
  │  ├─ IMoonrakerClient.cs (interface)
  │  ├─ MoonrakerClient.cs (implementation)
  │  ├─ MoonrakerStatusClient.cs
  │  ├─ MoonrakerSubscriptionService.cs
  │  ├─ MoonrakerDiagnosticsService.cs
  │  └─ MoonrakerBackendPlugin.cs (registers all above)
  ├─ Farm.Backend.Plugin.PrusaLink
  │  ├─ IPrusaLinkClient.cs (interface) ✓ Created
  │  ├─ PrusaLinkClient.cs ✓ Copied (needs namespace fix)
  │  ├─ PrusaLinkApiClient.cs ✓ Copied (needs namespace fix)
  │  ├─ PrusaLinkStatusClient.cs ✓ Copied (needs namespace fix)
  │  ├─ PrusaLinkPollingService.cs ✓ Copied (needs namespace fix)
  │  └─ PrusaLinkBackendPlugin.cs ✓ Rewritten (clean registration)
  ├─ Farm.Backend.Plugin.Sdcp
  │  ├─ ISdcpClient.cs (interface) ✓ Created
  │  ├─ SdcpClient.cs ✓ Copied (needs namespace fix)
  │  ├─ SdcpStatusClient.cs ✓ Copied (needs namespace fix)
  │  └─ SdcpBackendPlugin.cs (needs rewriting)
  └─ Farm.Backend.Plugin.OctoPrint
     ├─ IOctoPrintClient.cs (interface) ✓ Created
     ├─ OctoPrintClient.cs ✓ Copied (needs namespace fix)
     ├─ OctoPrintPollingService.cs ✓ Copied (needs namespace fix)
     ├─ OctoPrintWebSocketAdapter.cs ✓ Copied (needs namespace fix)
     ├─ OctoPrintStatusClient.cs ✓ Copied (needs namespace fix)
     └─ OctoPrintBackendPlugin.cs (needs rewriting)

Infrastructure (Farm.Infrastructure)
  └─ Contracts.Printers (shared interfaces)
     ├─ IPrinterStatusClient (base status client interface)
     ├─ IBackendClient (base client interface)
     └─ ISupportsXxx (capability interfaces)
```

## Implementation Status

### ✅ COMPLETED
1. **BackendClientFactory** - Refactored to use `IServiceProvider` + `IBackendPluginRegistry` for dynamic resolution
2. **ServiceCollectionExtensions** - Updated plugin registration to pass `IServiceProvider` to factory
3. **Moonraker Plugin** - Fully migrated (interface + implementation)
4. **Interface Creation** - All client interfaces (IMoonrakerClient, IPrusaLinkClient, ISdcpClient, IOctoPrintClient) created in plugins

### 🔄 IN PROGRESS
1. **PrusaLink Migration**
   - ✓ Files copied to plugin
   - ✓ Namespaces updated
   - ✓ Plugin registration rewritten
   - ✓ Project dependencies added
   - ⏳ Remaining: Fix any lingering import issues, test compilation

2. **Project File Updates**
   - ✓ PrusaLink.csproj - Added missing references
   - ✓ Sdcp.csproj - Added missing references
   - ✓ OctoPrint.csproj - Added missing references

### ⏳ TODO

#### Phase 1: SDCP Migration
1. **Namespace Updates** (similar to PrusaLink):
   - `SdcpClient.cs`: Change `Farm.Web.Api.Services` → `Farm.Backend.Plugin.Sdcp`
   - `SdcpStatusClient.cs`: Change `Farm.Web.Api.Services.Printers` → `Farm.Backend.Plugin.Sdcp`
   - Update imports: Remove `Farm.Web.Api.Services.Interfaces`, add `Farm.Infrastructure.Services.Printers`

2. **SdcpBackendPlugin.cs Rewrite**:
   ```csharp
   public Type ClientType => typeof(SdcpClient);
   public Type ClientInterfaceType => typeof(ISdcpClient);
   public Type? StatusClientType => typeof(SdcpStatusClient);
   public Type? StatusClientInterfaceType => typeof(IPrinterStatusClient);

   public void RegisterAdditionalServices(IServiceCollection services)
   {
       services.AddScoped<ISdcpClient, SdcpClient>();
       services.AddSingleton<IPrinterStatusClient, SdcpStatusClient>();
   }
   ```

3. **Verify Compilation** - Run: `dotnet build ./backends/Farm.Backend.Plugin.Sdcp/ -c Debug`

#### Phase 2: OctoPrint Migration
1. **Namespace Updates** (similar to PrusaLink):
   - `OctoPrintClient.cs`: Change `Farm.Web.Api.Services` → `Farm.Backend.Plugin.OctoPrint`
   - `OctoPrintPollingService.cs`: Change `Farm.Web.Api.Services` → `Farm.Backend.Plugin.OctoPrint`
   - `OctoPrintWebSocketAdapter.cs`: Change `Farm.Web.Api.Services` → `Farm.Backend.Plugin.OctoPrint`
   - `OctoPrintStatusClient.cs`: Change `Farm.Web.Api.Services.Printers` → `Farm.Backend.Plugin.OctoPrint`
   - Update imports: Remove API references, add `Farm.Infrastructure`

2. **OctoPrintBackendPlugin.cs Rewrite**:
   ```csharp
   public Type ClientType => typeof(OctoPrintClient);
   public Type ClientInterfaceType => typeof(IOctoPrintClient);
   public Type? StatusClientType => typeof(OctoPrintStatusClient);
   public Type? StatusClientInterfaceType => typeof(IPrinterStatusClient);

   public void RegisterAdditionalServices(IServiceCollection services)
   {
       services.AddHttpClient<IOctoPrintClient, OctoPrintClient>(client =>
       {
           client.Timeout = TimeSpan.FromSeconds(10);
       });
       services.AddSingleton<IPrinterStatusClient, OctoPrintStatusClient>();
       services.AddSingleton<IHostedService, OctoPrintPollingService>();
   }
   ```

3. **Verify Compilation** - Run: `dotnet build ./backends/Farm.Backend.Plugin.OctoPrint/ -c Debug`

#### Phase 3: API Refactoring - Migrate from Client-Specific to Capability Interfaces
**KEY ARCHITECTURAL CHANGE**: API should NOT use `IMoonrakerClient`, `IPrusaLinkClient`, `ISdcpClient`, or `IOctoPrintClient`. Instead, use generic `IBackendClient` with capability interface checking.

1. **Delete Client-Specific Interface Files from API**:
   - `/src/api/Services/Interfaces/IMoonrakerClient.cs`
   - `/src/api/Services/Interfaces/IPrusaLinkClient.cs`
   - `/src/api/Services/Interfaces/ISdcpClient.cs`
   - `/src/api/Services/Interfaces/IOctoPrintClient.cs`
   - `/src/api/Services/Interfaces/IPrusaLinkApiClient.cs`

2. **Refactor API Code to Use Capability Interfaces**:
   
   **Before** (client-specific):
   ```csharp
   var moonrakerClient = GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker);
   await moonrakerClient.HomeXYAsync(url, ct);
   ```
   
   **After** (capability-based):
   ```csharp
   var client = GetBackendClient(PrinterBackend.Moonraker);
   if (client is ISupportsMovement movement)
   {
       await movement.HomeAsync(url, apiKey, ct);
   }
   ```

3. **Update Files**:
   - **PrintersService.cs**: Replace all `GetBackendClient<IXxxClient>()` calls with generic `IBackendClient` + capability checks
   - **MoonrakerStatusClient.cs**: Update injected type from `IMoonrakerClient` → `IBackendClient` + cast to `ISupportsCamera`, etc.
   - **PrusaLinkStatusClient.cs**: Similar refactoring
   - **SdcpStatusClient.cs**: Similar refactoring
   - **OctoPrintStatusClient.cs**: Similar refactoring
   - **PrusaLinkPollingService.cs**: Refactor to use capabilities instead of `IPrusaLinkClient`
   - **HarvestWorkerService.cs**: Refactor to use capabilities instead of client-specific types
   - **PrinterCapabilityDiscoveryService.cs**: Refactor to check capabilities instead of casting to client types

4. **Verify API builds without backend code**:
   ```bash
   dotnet build ./api/Farm.Web.Api.csproj -c Debug
   ```
   Should have NO compile-time dependencies on backend clients - only on `IBackendClient` and capability interfaces.

#### Phase 4: Full Solution Build & Test
1. **Build Full Solution**:
   ```bash
   dotnet build ./farm-web.sln -c Debug
   ```

2. **Run All Tests**:
   ```bash
   dotnet test ./farm-web.sln -c Debug
   ```

3. **Integration Test - Verify Plugin Loading & Capabilities**:
   - Run API server
   - Check logs for plugin discovery messages
   - Verify BackendClientFactory successfully loads all clients from plugins
   - Verify capability detection works: All backends report supported capabilities
   - Test API endpoints to ensure all backends work with capability-based operations

## Key Technical Decisions

### 1. Capability-Based API Architecture
**Decision**: API uses `IBackendClient` + capability interface checking instead of client-specific interfaces.

**Pattern**:
```csharp
// Get generic backend client from plugin
var client = _factory.GetBackendClient(PrinterBackend.Moonraker);

// Check for specific capability and use it
if (client is ISupportsCamera camera)
{
    var url = await camera.GetCameraStreamUrlAsync(baseUrl, ct);
}

if (client is ISupportsMovement movement)
{
    await movement.HomeAsync(baseUrl, apiKey, ct);
}
```

**Rationale**:
- Client-specific interfaces (`IMoonrakerClient`, `IPrusaLinkClient`, etc.) should ONLY exist in their backend plugins
- API has NO compile-time dependencies on any specific backend implementation
- Capability interfaces live in `Farm.Infrastructure.Services.Printers` and are the contract between API and backends
- Each capability interface defines methods needed for that feature (e.g., ISupportsCamera has GetCameraStreamUrlAsync)
- Different backends can implement different capabilities
- API code is resilient to new backends being added

### 2. Factory Pattern for Dynamic Resolution
**Before**: `BackendClientFactory(IMoonrakerClient moon, IPrusaLinkClient prusa, ...)`
- Problem: API had compile-time dependencies on all client types
- Could not add new backends without modifying API

**After**: `BackendClientFactory(IServiceProvider sp, IBackendPluginRegistry registry, ...)`
- Solution: Uses dependency injection to resolve any backend interface registered by plugins
- Completely plugin-driven
- Can add new backends by only adding a new plugin

### 3. Capability Interface Definitions
**Location**: `Farm.Infrastructure.Services.Printers/IBackendClientCapabilities.cs`

**Interface Examples**:
- `ISupportsCamera` - Defines GetCameraStreamUrlAsync(), GetCameraSnapshotUrlAsync()
- `ISupportsMovement` - Defines HomeAsync(), MoveAsync()
- `ISupportsFileUpload` - Defines UploadGcodeAsync()
- `ISupportsTemperatureControl` - Defines SetTemperaturesAsync()
- `ISupportsStartPrint` - Defines StartPrintAsync()
- `ISupportsHistory` - Defines GetHistoryListAsync(), GetHistoryJobAsync(), etc.

Backend implementations cast themselves to these interfaces to indicate which features they support.

### 4. SignalR Hub Location
- **Location**: `Farm.SignalR.Hubs.PrinterHub`
- **Why**: Both API and plugins need this, so neutral intermediate library
- **Access**: Re-exported in API as `global using PrinterHub = Farm.SignalR.Hubs.PrinterHub;`

### 5. Status Client Registration
- All status clients implement `IPrinterStatusClient` (from Infrastructure.Contracts)
- Registered as `IPrinterStatusClient` (not specific typed like `IMoonrakerStatusClient`)
- Factory discovers them dynamically from plugin registry, avoiding hard types

## File-by-File Changes Reference

### PrusaLink Files - Change Summary
1. **PrusaLinkClient.cs**
   - Line 6: `using Farm.Web.Api.Services.Interfaces;` → `using Farm.Infrastructure.Services.Printers;`
   - Line 8: `namespace Farm.Web.Api.Services;` → `namespace Farm.Backend.Plugin.PrusaLink;`

2. **PrusaLinkApiClient.cs**
   - Line 7: `using Farm.Web.Api.Services.Interfaces;` → `using Farm.Infrastructure.Services.Printers;`
   - Line 9: `namespace Farm.Web.Api.Services;` → `namespace Farm.Backend.Plugin.PrusaLink;`

3. **PrusaLinkPollingService.cs**
   - Line 6: `using Farm.Web.Api.Services.Interfaces;` → Delete
   - Line 7: Change `Farm.Web.Api.Hubs;` → `Farm.SignalR.Hubs;`
   - Line 10: `namespace Farm.Web.Api.Services;` → `namespace Farm.Backend.Plugin.PrusaLink;`

4. **PrusaLinkStatusClient.cs**
   - Line 8: `using Farm.Web.Api.Services.Interfaces;` → `using Farm.Infrastructure.Services.Printers;`
   - Line 10: `namespace Farm.Web.Api.Services.Printers` → `namespace Farm.Backend.Plugin.PrusaLink`

5. **PrusaLinkBackendPlugin.cs**
   - Replace entire RegisterAdditionalServices method to use local types
   - Replace GetCapabilities to use typeof() instead of string reflection
   - Remove GetTypeFromApi() method

## Verification Checklist

### After Each Plugin Migration
- [ ] Plugin project builds successfully
- [ ] No compilation errors in plugin
- [ ] No lingering `Farm.Web.Api` references
- [ ] No lingering `Farm.Web.Api.Services.Interfaces` imports
- [ ] All services registered in RegisterAdditionalServices()
- [ ] No reflection-based type loading (use typeof directly)

### After Full Migration
- [ ] Full solution builds without errors
- [ ] No API files reference backend-specific classes
- [ ] API project has NO dependencies on backend plugins
- [ ] All tests pass
- [ ] Plugin discovery logs show all 4 backends loaded
- [ ] Each backend works via API endpoints (test with curl or Postman)

## Timeline Estimate
- **PrusaLink**: 30 minutes (files copied, 80% done - just need namespace fixes)
- **SDCP**: 30 minutes (same pattern as PrusaLink)
- **OctoPrint**: 45 minutes (more files, WebSocket adapter complicates slightly)
- **API Cleanup**: 15 minutes (delete files)
- **Testing**: 30 minutes (build, test, integration verify)

**Total**: ~2.5 hours for complete migration

## Success Criteria
1. ✅ API builds with ZERO references to client-specific interfaces (IMoonrakerClient, IPrusaLinkClient, etc.)
2. ✅ API code uses capability interfaces for all backend operations
3. ✅ All 4 plugins build independently
4. ✅ Full solution builds without errors
5. ✅ All unit/integration tests pass
6. ✅ Plugin discovery successfully loads all 4 backends
7. ✅ BackendClientFactory dynamically resolves all clients from plugins
8. ✅ Each backend functional through capability-based API calls
9. ✅ No reflection-based type loading in production code
10. ✅ API code uses pattern: `if (client is ISupportsXxx capability) { ... }` instead of client-specific casts
