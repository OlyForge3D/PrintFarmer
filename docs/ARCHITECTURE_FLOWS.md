# Architecture Deep Dives

Detailed request flows and system interactions for advanced understanding of PrintFarmer internals.

## Printer Discovery Flow

### Overview
The printer discovery system implements a backend selection pattern allowing users to choose which printer APIs to scan during network discovery.

### User Interaction (Frontend)
**File**: `src/Web/ReactApp/src/components/PrinterDiscoveryModal.tsx`

```typescript
// User selects backends to scan
const [selectedBackends, setSelectedBackends] = useState<Set<PrinterBackend>>(
  new Set([PrinterBackend.Moonraker, PrinterBackend.PrusaLink])
);

// When user clicks "Start Network Scan"
const handleStartDiscovery = async () => {
  const backends = selectedBackends.size > 0 
    ? Array.from(selectedBackends) 
    : undefined;
  
  const result = await startDiscoveryMutation.mutateAsync({ backends });
  setSessionId(result.sessionId);
};
```

### API Flow (Backend)
**File**: `src/api/Controllers/PrintersController.cs`

```csharp
[HttpPost("discover/stream")]
public ActionResult StartDiscoveryStream(
    [FromBody] StartDiscoveryRequest? request, 
    CancellationToken ct)
{
    string sessionId = Guid.NewGuid().ToString();
    
    // Pass backend selection to service
    await _networkDiscovery.DiscoverPrintersWithProgressAsync(
        sessionId, 
        request?.Backends,  // User-selected backends
        timeoutCts.Token
    );
    
    return Ok(new { sessionId });
}
```

### Network Discovery Service
**File**: `src/api/Services/NetworkDiscoveryService.cs`

The service filters discovery by selected backends:

```csharp
public async Task DiscoverPrintersWithProgressAsync(
    string sessionId, 
    List<PrinterBackend>? backends,
    CancellationToken cancellationToken = default)
{
    var settings = _settingsService.GetSettings();
    
    // Use user-selected backends or defaults
    if (backends != null && backends.Count > 0)
    {
        settings = settings with { Backends = backends };
    }
    
    // Scan network with selected backends
    foreach (var host in networkHosts)
    {
        var discovered = await ScanHostAsync(host, settings, cancellationToken);
        if (discovered != null)
        {
            await BroadcastProgressAsync(sessionId, discovered);
        }
    }
}
```

### Host Scanning
For each host, the service tries selected backends:

```csharp
private async Task<DiscoveredPrinterDto?> TryDiscoverPrinterAsync(
    string ipAddress, 
    int port, 
    List<PrinterBackend>? backends)
{
    // Try each selected backend
    foreach (var backend in backends ?? DefaultBackends)
    {
        if (backend == PrinterBackend.Moonraker && port == 7125)
        {
            var info = await TryGetMoonrakerInfoAsync(ipAddress, port);
            if (info != null) return CreateDiscoveredPrinter(info, backend);
        }
        else if (backend == PrinterBackend.PrusaLink && port == 80)
        {
            var info = await TryGetPrusaLinkInfoAsync(ipAddress, port);
            if (info != null) return CreateDiscoveredPrinter(info, backend);
        }
    }
    return null;
}
```

### SignalR Real-time Progress
**File**: `src/api/Hubs/PrinterHub.cs`

Discovery progress is broadcast via SignalR:

```csharp
private async Task BroadcastProgressAsync(string sessionId, DiscoveredPrinterDto discovered)
{
    await Clients.All.SendAsync("discoveryprogress", new DiscoveryProgressDto
    {
        SessionId = sessionId,
        DiscoveredPrinters = discovered,
        Timestamp = DateTime.UtcNow
    });
}
```

### Frontend Real-time Updates
**File**: `src/Web/ReactApp/src/services/printer-signalr.ts`

```typescript
connection.on('discoveryprogress', (progress: DiscoveryProgressDto) => {
  // Update UI with newly discovered printers
  setPrinters(prev => [...prev, ...progress.discoveredPrinters]);
  setProgress(progress);
});
```

## Heartbeat Architecture

### Overview
The printer discovery service implements active health monitoring to confirm it's running. This prevents the UI from showing disabled or unresponsive services.

### Discovery Service (Sender)
**File**: `src/printer-discovery/Services/HeartbeatBackgroundService.cs`

```csharp
public class HeartbeatBackgroundService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(5000, stoppingToken);  // Initial 5s delay
        
        var interval = TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds ?? 30);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Send heartbeat to API
                var response = await _client.PostAsJsonAsync(
                    $"{_settings.ApiBaseUrl}/api/settings/NetworkDiscovery/heartbeat",
                    new { timestamp = DateTime.UtcNow },
                    stoppingToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Heartbeat failed: {ex.Message}");
                // Continue on next interval
            }
            
            await Task.Delay(interval, stoppingToken);
        }
    }
}
```

### API Server (Receiver)
**File**: `src/api/Settings/NetworkDiscoverySettings.cs`

```csharp
public class NetworkDiscoverySettings
{
    public DateTime? LastHeartbeat { get; set; }
    public bool IsServiceRunning => 
        LastHeartbeat.HasValue && 
        DateTime.UtcNow.Subtract(LastHeartbeat.Value).TotalSeconds < 90;
}
```

**File**: `src/api/Controllers/SettingsController.cs`

```csharp
[HttpPost("NetworkDiscovery/heartbeat")]
public IActionResult RecordHeartbeat()
{
    var settings = _settingsService.GetSettings();
    settings.LastHeartbeat = DateTime.UtcNow;
    _settingsService.SaveSettings(settings);
    
    return Ok();
}
```

### UI Health Check
**File**: `src/Web/ReactApp/src/components/DiscoveryStatus.tsx`

```typescript
const isServiceRunning = useQuery({
  queryKey: ['discovery-status'],
  queryFn: async () => {
    const response = await api.get('/api/settings/NetworkDiscovery');
    return response.data.isServiceRunning;
  },
  refetchInterval: 30000,
});

return (
  <div>
    {isServiceRunning ? (
      <span>✅ Discovery Service Running</span>
    ) : (
      <span>❌ Service Offline (Check Docker)</span>
    )}
  </div>
);
```

## PrusaLink Status Flow

### Overview
The PrusaLink client integration implements a specialized status flow for Prusa 3D printers.

### Status Polling
**File**: `src/api/Services/BackendClients/PrusaLinkClient.cs`

```csharp
public async Task<PrinterStatus> GetStatusAsync(string apiKey, string host)
{
    // PrusaLink provides unified status endpoint
    var response = await _client.GetAsync<PrusaLinkStatus>($"http://{host}/api/v1/status");
    
    return new PrinterStatus
    {
        State = MapPrusaState(response.State),
        Temperature = response.Nozzle?.ActualTemp ?? 0,
        TargetTemperature = response.Nozzle?.TargetTemp ?? 0,
        BedTemperature = response.Bed?.ActualTemp ?? 0,
        TargetBedTemperature = response.Bed?.TargetTemp ?? 0,
        Progress = response.Progress?.Progress ?? 0,
        Timestamp = DateTime.UtcNow
    };
}
```

### Real-time Updates via SignalR
Status updates flow through SignalR hub to connected clients:

```csharp
private async Task UpdatePrinterStatusAsync(string printerId, PrinterStatus status)
{
    await _hub.Clients.All.SendAsync("printerupdated", new PrinterStatusUpdate
    {
        PrinterId = printerId,
        State = status.State,
        Temperature = status.Temperature,
        Progress = status.Progress,
        Timestamp = status.Timestamp
    });
}
```

### Frontend Real-time Handling
**File**: `src/Web/ReactApp/src/services/printer-signalr.ts`

```typescript
connection.on('printerupdated', (update: PrinterStatusUpdate) => {
  // Update printer store with real-time status
  setPrinterStatus(update.printerId, {
    state: update.state,
    temperature: update.temperature,
    progress: update.progress,
  });
});
```

## Request/Response Patterns

### Event-Driven SignalR Communication
All real-time updates use **lowercase event names** for consistency:

```typescript
// Send event (backend)
await Clients.All.SendAsync("printerupdated", data);

// Listen for event (frontend)
connection.on('printerupdated', (data) => { /* ... */ });
```

**Event Names**:
- `printerupdated` - Printer status change
- `discoveryprogress` - Discovery session progress
- `slicingcompleted` - Slicing job completed
- `jobqueued` - Job added to queue
- `jobcompleted` - Job completed

### JSON Naming Convention
All API responses use **camelCase** for JSON properties:

**C#** (PascalCase in code):
```csharp
public class PrinterStatus
{
    public string Name { get; set; }
    public bool IsOnline { get; set; }
}
```

**JSON Response** (camelCase via JsonSerializerOptions):
```json
{
  "name": "Printer 1",
  "isOnline": true
}
```

This is configured globally in `Program.cs`:
```csharp
.AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
})
```

## Background Services Architecture

PrintFarmer uses background services for long-running operations:

### Service Types

1. **HostedService** - Singleton background tasks
   - `HeartbeatBackgroundService` - Discovery service health monitoring
   - `MoonrakerSubscriptionService` - Real-time printer polling

2. **Scoped Services** - Called by hosted services
   - `PrinterService` - State management
   - `NetworkDiscoveryService` - Host discovery

3. **Workers** - External microservice containers
   - OrcaSlicer Worker - Distributed gcode generation
   - Printer Discovery Service - Isolated discovery

### Service Lifecycle
1. DI container creates hosted services
2. `StartAsync()` called on application startup
3. Service runs continuously (`ExecuteAsync()`)
4. `StopAsync()` called on application shutdown
5. Resources cleaned up and disposed

See [Deployment Guide](./DEPLOYMENT.md) for distributed worker configuration.
