# Backend Client Interface Refactoring Plan

## Analysis of Method Calls

### Methods by Backend and Functionality

#### Common Methods (all or most backends support)
- `GetStatusAsync(baseUrl)` - All backends
- `GetJobAsync(baseUrl)` - All backends (Moonraker, SDCP, OctoPrint)
- `GetCompositeStatusAsync(baseUrl)` - Moonraker, SDCP
- `GetFileListAsync(baseUrl, apiKey)` - All backends

#### Moonraker-Specific Methods
- `GetSpoolmanActiveSpoolAsync(baseUrl)`
- `GetSpoolmanSpoolByIdAsync(baseUrl, spoolId)`
- `SendHomeAsync(baseUrl)` - Movement
- `HomeXYAsync(baseUrl)` - Movement
- `HomeZAsync(baseUrl)` - Movement (not in OctoPrint)
- `SetTempsAsync(baseUrl, hotend, bed)` - Temperature
- `MoveAsync(baseUrl, x, y, z, f)` - Movement
- `MoveToAsync(baseUrl, x, y, z, f)` - Movement
- `FirmwareRestartAsync(baseUrl)`
- `SendGcodeAsync(baseUrl, gcode)`
- `GetFileMetadataAsync(baseUrl, filename)` - Metadata

#### OctoPrint-Specific Methods
- `SendHomeAsync(baseUrl, apiKey)` - Movement (same name as Moonraker but different signature)
- `HomeXYAsync(baseUrl, apiKey)` - Movement
- `HomeZAsync(baseUrl, apiKey)` - Movement
- `SetBedTempAsync(baseUrl, apiKey, bedTemp)` - Temperature
- `SetHotendTempAsync(baseUrl, apiKey, hotendTemp, tool)` - Temperature

#### PrusaLink-Specific Methods
- `GetFileListAsync(baseUrl, apiKey)`

#### SDCP-Specific Methods
- `GetFileListAsync(baseUrl)`
- `GetJobAsync(baseUrl)`

### New Interface Architecture

#### 1. IBackendClient - Common Interface (in Infrastructure.Contracts.Printers)
```csharp
public interface IBackendClient
{
    // Marker interface - implementations should expose their capabilities via other interfaces
}
```

#### 2. Capability Interfaces (in Infrastructure.Services.Printers)

**ISupportsStatus** - Basic printer status
```csharp
Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default);
```

**ISupportsCompositeStatus** - Advanced status (Moonraker, SDCP)
```csharp
Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default);
```

**ISupportsSpoolman** - Moonraker spoolman integration
```csharp
Task<int?> GetSpoolmanActiveSpoolAsync(string baseUrl, CancellationToken ct = default);
Task<string?> GetSpoolmanSpoolByIdAsync(string baseUrl, int spoolId, CancellationToken ct = default);
```

**ISupportsMovement** - Expand existing to include Moonraker-specific methods
```csharp
// Moonraker specific
Task<bool> SendHomeAsync(string baseUrl, CancellationToken ct = default);
Task<bool> HomeXYAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
Task<bool> HomeZAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
Task<bool> MoveAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, string? apiKey = null, CancellationToken ct = default);
Task<bool> MoveToAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, string? apiKey = null, CancellationToken ct = default);
```

**ISupportsTemperatureControl** - Expand existing
```csharp
// Generic method
Task<bool> SetTemperaturesAsync(string baseUrl, double? hotendTemp = null, double? bedTemp = null, string? apiKey = null, CancellationToken ct = default);

// OctoPrint specific
Task<bool> SetBedTempAsync(string baseUrl, string apiKey, double bedTemp, CancellationToken ct = default);
Task<bool> SetHotendTempAsync(string baseUrl, string apiKey, double hotendTemp, string tool = "tool0", CancellationToken ct = default);
```

**ISupportsFileList** - Expand existing
```csharp
// All backends support this with different signatures
Task<List<PrinterFileInfo>> GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
```

**ISupportsFileMetadata** - Expand existing (Moonraker)
```csharp
Task<GCodeMetadata?> GetFileMetadataAsync(string baseUrl, string filePath, string? apiKey = null, CancellationToken ct = default);
```

**ISupportsJobControl** - Job status (all backends)
```csharp
Task<PrinterJob?> GetJobAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
```

**ISupportsControlRestart** - Firmware/system restart
```csharp
Task<bool> FirmwareRestartAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
```

**ISupportsGcodeExecution** - Send raw gcode (Moonraker)
```csharp
Task<bool> SendGcodeAsync(string baseUrl, string gcode, string? apiKey = null, CancellationToken ct = default);
```

## Implementation Strategy

### Phase 1: Expand Capability Interfaces
1. Update `IBackendClientCapabilities.cs` in Infrastructure to include all backend-specific methods
2. Add optional `apiKey` parameters where needed for consistency across backends

### Phase 2: Update Backend Implementations
1. Moonraker plugin: Already implements capabilities - just verify it covers all methods
2. PrusaLink plugin: Update to implement expanded capabilities
3. SDCP plugin: Update to implement expanded capabilities
4. OctoPrint plugin: Update to implement expanded capabilities

### Phase 3: Update API to Use Capabilities
1. **PrintersService.cs**: Remove `GetBackendClient<TClient>()` method, replace all calls with capability-based approach
2. **Status Clients**: Update to receive `IBackendClient` instead of specific types
3. **HarvestWorkerService.cs**: Update to use capabilities
4. **PrinterCapabilityDiscoveryService.cs**: Update to check capabilities instead of casting to specific types

### Phase 4: Cleanup
1. Delete `IPrusaLinkApiClient.cs` (if still exists)
2. Verify no references to client-specific interfaces remain
3. Update imports to use Infrastructure.Services.Printers for all capability interfaces

## Code Pattern Examples

### Old Pattern (Remove)
```csharp
var client = GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker);
await client.SendHomeAsync(url, ct);
```

### New Pattern (Adopt)
```csharp
var client = _backendFactory.GetClient(PrinterBackend.Moonraker);
if (client is ISupportsMovement movement)
{
    await movement.SendHomeAsync(url, ct: ct);
}
```

### For backend-specific needs
```csharp
var client = _backendFactory.GetClient(PrinterBackend.Moonraker);
if (client is ISupportsSpoolman spoolman)
{
    var spoolId = await spoolman.GetSpoolmanActiveSpoolAsync(url, ct);
}
```

## Success Criteria
1. All capability interfaces defined with full method signatures
2. All backend implementations updated to match new interfaces
3. All API code uses capability-based pattern
4. No client-specific interfaces referenced in API
5. Full solution compiles without errors
6. All tests pass
