# Plugin System Architecture Refactoring

## Overview

This document outlines the refactoring initiative to migrate backend client implementations from the API project into respective plugin projects. This improves separation of concerns, modularity, and enables a true plugin architecture.

## Current Status

✅ **Phase 1: Plugin System Extensions (COMPLETED)**
- Created `IExtendedBackendPlugin` interface for plugins with additional services
- Extended `IBackendPluginRegistry` with methods for accessing extended plugins
- Updated `BackendPluginRegistry` implementation to support extended plugins
- Updated all backend plugins to implement `IExtendedBackendPlugin`
- Enhanced `BackendPluginExtensions` to discover and register extended plugins
- **Build Status**: ✅ Passes (0 errors, 151 warnings)
- **Test Status**: ✅ 1971 tests pass (2 skipped, 0 failures)

## Architecture Changes

### Plugin Extension Interface

A new `IExtendedBackendPlugin` interface extends `IBackendClientPlugin` with:

```csharp
public interface IExtendedBackendPlugin : IBackendClientPlugin
{
    Type? StatusClientType { get; }
    Type? StatusClientInterfaceType { get; }
    void RegisterAdditionalServices(IServiceCollection services);
    IEnumerable<string> GetConfigurationSections();
}
```

This allows plugins to:
- Declare status client implementations
- Register additional backend-specific services
- List configuration sections they consume

### Updated Plugin Registry

Extended `IBackendPluginRegistry` with new methods:

```csharp
IExtendedBackendPlugin? GetExtendedPlugin(string backendType);
IEnumerable<IExtendedBackendPlugin> GetAllExtendedPlugins();
```

### Backend Plugin Updates

All four backend plugins now implement `IExtendedBackendPlugin`:

1. **Moonraker Plugin** (`Farm.Backend.Plugin.Moonraker`)
   - Client: `MoonrakerClient`
   - Status Client: `MoonrakerStatusClient`
   - Capabilities: FileList, FileDownload, FileUpload, StartPrint, History, Temperature, Movement, etc.

2. **OctoPrint Plugin** (`Farm.Backend.Plugin.OctoPrint`)
   - Client: `OctoPrintClient`
   - Status Client: `OctoPrintStatusClient`
   - Capabilities: FileList, FileDownload, FileUpload, StartPrint, History, Temperature, Control, Camera

3. **PrusaLink Plugin** (`Farm.Backend.Plugin.PrusaLink`)
   - Client: `PrusaLinkClient`
   - Status Client: `PrusaLinkStatusClient`
   - Capabilities: FileList, FileUpload, StartPrint, History, Temperature, Control, Camera, FileMetadata

4. **SDCP Plugin** (`Farm.Backend.Plugin.Sdcp`)
   - Client: `SdcpClient`
   - Status Client: `SdcpStatusClient`
   - Capabilities: FileList, FileUpload, StartPrint, History, Temperature, Movement, Control

## Migration Roadmap

### Phase 2: Status Client Migration (IN PROGRESS)

**Objective**: Move status client implementations from API to respective plugins while maintaining the existing abstraction layer.

Current Status Clients in API:
- `/src/api/Services/Printers/MoonrakerStatusClient.cs`
- `/src/api/Services/Printers/OctoPrintStatusClient.cs`
- `/src/api/Services/Printers/PrusaLinkStatusClient.cs`
- `/src/api/Services/Printers/SdcpStatusClient.cs`

**Approach**:
1. Each plugin will include its status client implementation
2. Plugins will register their status clients via `RegisterAdditionalServices`
3. The `PrinterStatusClientFactory` will be updated to use plugin registry
4. Status client interfaces will remain in the Shared/Interfaces layer

**Expected Outcome**:
- API no longer directly references status clients
- Status clients are discovered and instantiated via plugins
- Cleaner dependency graph: API → Plugins → Status Clients

### Phase 3: Background Services Migration

**Objective**: Move backend-specific polling and subscription services into plugins.

Current Services to Migrate:
- `MoonrakerSubscriptionService`
- `OctoPrintPollingService`
- `PrusaLinkPollingService`

**Approach**:
1. Create service interfaces in plugin core
2. Move implementations to respective plugins
3. Update plugin's `RegisterAdditionalServices` to register these services
4. Remove from API's background services registration

### Phase 4: HTTP Client Configuration Migration

**Objective**: Move HTTP client configuration from API to plugins.

Current Registrations in `ServiceCollectionExtensions.RegisterHttpClients()`:
- `IMoonrakerClient` → `MoonrakerClient`
- `IOctoPrintClient` → `OctoPrintClient`
- `IPrusaLinkClient` → `PrusaLinkClient`
- `ISdcpClient` → `SdcpClient`

**Approach**:
1. Each plugin will register its own HTTP client and interface
2. `RegisterHttpClients` will become a coordinator that calls plugin methods
3. Reduce API's direct dependencies on backend implementations

### Phase 5: Capability Detection Consolidation

**Objective**: Move capability detection logic into plugins.

Current Implementation:
- `BackendCapabilityFactory` uses reflection to detect capabilities
- Plugins provide metadata via `GetCapabilities()`

**Enhancement**:
- Plugins will own their capability definition entirely
- API will use plugin metadata without reflection fallback
- Simplified and more maintainable capability model

## Service Registration Flow

Current flow (before migration):
```
Program.cs
  → ServiceCollectionExtensions.AddPrintFarmerServices()
    → RegisterPrinterServices()
      → Register factories and direct services
    → RegisterHttpClients()
      → Register each backend client
    → RegisterBackgroundServices()
      → Register polling/subscription services
```

Target flow (after migration):
```
Program.cs
  → ServiceCollectionExtensions.AddPrintFarmerServices()
    → RegisterBackendClientPlugins()
      → Discover plugins via registry
      → Call each plugin's RegisterAdditionalServices()
        → Status clients registered
        → Background services registered
        → HTTP clients registered
    → RegisterPrinterServices()
      → Factories now use plugin registry
    → Remaining infrastructure services
```

## Benefits

1. **Separation of Concerns**: Each backend has its own plugin
2. **Modularity**: Easy to add new backends without modifying API
3. **Testability**: Can test each plugin independently
4. **Maintainability**: Backend code grouped logically
5. **Plugin Discovery**: Dynamic loading of backends at runtime
6. **Clear Boundaries**: Plugins define their own service registrations

## Testing Strategy

- **Unit Tests**: Each plugin tested independently
- **Integration Tests**: Verify plugin discovery works
- **Functional Tests**: Ensure backend functionality unchanged
- **Coverage**: Target 75%+ line coverage for all plugins

## Backward Compatibility

- All changes are additive (new interfaces don't remove existing ones)
- Existing service registration methods remain functional
- No breaking changes to public APIs during migration
- Gradual rollout per phase

## Files Modified

### Plugin Core
- `IExtendedBackendPlugin.cs` (NEW)
- `IBackendPluginRegistry.cs` (extended)
- `BackendPluginRegistry.cs` (extended)

### Backend Plugins
- `Farm.Backend.Plugin.Moonraker/MoonrakerBackendPlugin.cs` (updated)
- `Farm.Backend.Plugin.OctoPrint/OctoPrintBackendPlugin.cs` (updated)
- `Farm.Backend.Plugin.PrusaLink/PrusaLinkBackendPlugin.cs` (updated)
- `Farm.Backend.Plugin.Sdcp/SdcpBackendPlugin.cs` (updated)

### API
- `src/api/Extensions/BackendPluginExtensions.cs` (extended)
- `src/api/Infrastructure/ServiceCollectionExtensions.cs` (refactored registration)

## Next Steps

1. ✅ Complete Phase 1 (Plugin System Extensions)
2. ⏳ Phase 2: Move status clients to plugins
3. ⏳ Phase 3: Move background services to plugins
4. ⏳ Phase 4: Move HTTP client registration to plugins
5. ⏳ Phase 5: Consolidate capability detection

## Validation Checklist

- [x] Build succeeds (0 errors)
- [x] All tests pass (1971 passed)
- [x] Plugin registry accessible
- [x] Extended plugins discoverable
- [ ] Status clients moved to plugins
- [ ] Background services moved to plugins
- [ ] HTTP clients registered by plugins
- [ ] Capability detection unified

## Questions & Discussion

- Should we create a base plugin class to reduce code duplication?
- Should plugins be loaded from external assemblies in the future?
- Should we add plugin versioning and compatibility checks?
- Should we create plugin documentation generators?

---

**Status**: In Progress  
**Last Updated**: 2025-12-13  
**Owner**: Refactoring Initiative
