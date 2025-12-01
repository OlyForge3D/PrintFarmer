# PrusaLink Printer Status Flow Analysis

## Overview

This document outlines how PrusaLink printer status is retrieved and communicated to clients in the PrintFarmer backend architecture. Unlike Moonraker printers which use real-time WebSocket subscriptions, PrusaLink uses HTTP-based polling for status updates.

**Key Finding**: PrusaLink status updates are **request-driven** (on-demand via HTTP), not subscription-driven. There is no background service continuously polling PrusaLink printers for real-time updates.

---

## Architecture Summary

### Status Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                     React Client (Frontend)                          │
│                 http://localhost:3000                                │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                           │ HTTP Requests
                           ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    PrintersController                                │
│              /api/printers/{id}/status  (GET)                       │
│              /api/printers/{id}  (GET)                              │
│              /api/printers  (GET)                                   │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                           │ Service Call
                           ▼
┌─────────────────────────────────────────────────────────────────────┐
│               PrintersService                                        │
│    GetStatusDtoAsync()                                              │
│    GetPrinterDtoAsync()                                             │
│    GetAllWithStatusDtosAsync()                                      │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                    ┌──────┴──────┐
                    │             │
        For PrusaLink│             │For Moonraker/SDCP
                    ▼             │
    ┌──────────────────────────┐  │
    │  PrusaLinkClient         │  │ (MoonrakerSubscriptionService
    │  .GetCompositeStatusAsync│  │  sends via SignalR)
    │  .GetStatusAsync         │  │
    │  .GetJobAsync            │  │
    └──────────────────────────┘  │
                    │              │
                    ▼              ▼
         HTTP GET to Printer    WebSocket or HTTP
         /api/status            (continuous)
         /api/job               ▼
                            SignalR Hub
                            (PrinterHub)
                            SendAsync("printerupdated")
```

---

## Component Details

### 1. Client Entry Points

#### **PrintersController** (`src/api/Controllers/PrintersController.cs`)

**Endpoint**: `GET /api/printers/{id:guid}/status`
```csharp
[HttpGet("{id:guid}/status")]
[ProducesResponseType(typeof(PrinterStatusDto), 200)]
public async Task<ActionResult<PrinterStatusDto>> GetStatusAsync(Guid id, CancellationToken ct)
{
    try
    {
        PrinterStatusDto dto = await _printersService.GetStatusDtoAsync(id, ct);
        return Ok(dto);
    }
    catch (KeyNotFoundException)
    {
        return NotFound();
    }
    catch (Exception ex)
    {
        _logger.LogWarning($"Error getting status for printer {id}: {ex.Message}");
        return new PrinterStatusDto(Id: id, IsOnline: false, ...);
    }
}
```

**Endpoint**: `GET /api/printers/{id:guid}`
```csharp
[HttpGet("{id:guid}", Name = "GetPrinterById")]
public async Task<ActionResult<PrinterDto>> GetAsync(Guid id, CancellationToken ct)
{
    try
    {
        PrinterDto dto = await _printersService.GetPrinterDtoAsync(id, ct);
        return Ok(dto);
    }
    // Error handling...
}
```

**Endpoint**: `GET /api/printers`
```csharp
[HttpGet]
public async Task<ActionResult<IEnumerable<PrinterFastDto>>> GetAsync(
    CancellationToken ct, 
    [FromQuery] bool includeDisabled = false)
{
    // Returns lightweight list with basic status info
}
```

---

### 2. Service Layer: PrintersService

**File**: `src/api/Services/Printers/PrintersService.cs`

#### **GetStatusDtoAsync** (Lines 273-317)

Retrieves current status for a single printer. Uses **circuit breaker pattern** with 3-second timeout.

```csharp
public async Task<PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct)
{
    Printer? p = await _repo.FindByIdAsync(id, ct);
    if (p is null)
        throw new KeyNotFoundException();

    using CancellationTokenSource statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    statusCts.CancelAfter(TimeSpan.FromSeconds(3));  // 3-second timeout

    try
    {
        if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
        {
            // Get circuit breaker for this PrusaLink printer
            CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
            
            // Call PrusaLink client to fetch status
            PrusaCompositeStatus status = await breaker.ExecuteAsync(
                async ct => await _prusa.GetCompositeStatusAsync(
                    p.ServerUrl,    // e.g., "http://printer-ip" or "192.168.1.100"
                    p.ApiKey,       // Optional API key from database
                    ct
                ), 
                statusCts.Token
            );

            // Return DTO with status data
            return new PrinterStatusDto(
                Id: p.Id,
                IsOnline: status.IsOnline,
                State: status.State,
                Progress: status.Progress,
                JobName: status.JobName,
                ThumbnailUrl: status.ThumbnailUrl,
                CameraStreamUrl: status.CameraStreamUrl,
                CameraSnapshotUrl: status.CameraSnapshotUrl
            );
        }
        // ... Moonraker/SDCP branches
    }
    catch (OperationCanceledException) when (statusCts.Token.IsCancellationRequested)
    {
        _logger.LogWarning($"Status timeout for printer {p.Id}");
        return new PrinterStatusDto(Id: p.Id, IsOnline: false, ...);
    }
    catch (Exception ex)
    {
        _logger.LogWarning($"Error getting status for printer {p.Id}: {ex.Message}");
        return new PrinterStatusDto(Id: p.Id, IsOnline: false, ...);
    }
}
```

**Key Points**:
- **Timeout**: 3 seconds per request
- **Circuit Breaker**: Per-printer circuit breaker to prevent cascading failures
- **Error Handling**: Returns offline status on any error or timeout
- **Backend Detection**: Checks printer backend to route to correct client

#### **GetPrinterDtoAsync** (Lines 318-338)

Retrieves comprehensive printer info with status.

```csharp
public async Task<PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct)
{
    Printer? p = await _repo.FindByIdWithIncludesAsync(id, ct);
    if (p is null)
        throw new KeyNotFoundException();

    if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
    {
        // Fetch status from PrusaLink
        PrusaCompositeStatus status = await _prusa.GetCompositeStatusAsync(
            p.ServerUrl, 
            p.ApiKey, 
            ct
        );
        
        // Delegate DTO creation to PrusaLink client
        return await _prusa.CreatePrinterDtoAsync(p, status, ct);
    }
    // ... other backends
}
```

#### **GetAllWithStatusDtosAsync** (Lines 215-269)

Retrieves all printers with status info (used for dashboard).

```csharp
public async Task<PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct)
{
    List<Printer> items = await _repo.GetAllWithIncludesAsync(ct);
    
    using CancellationTokenSource fastTimeoutCts = 
        CancellationTokenSource.CreateLinkedTokenSource(ct);
    fastTimeoutCts.CancelAfter(TimeSpan.FromSeconds(1));  // 1-second timeout per printer
    
    PrinterDto[] dtos = await Task.WhenAll(
        items.Select(async p =>
        {
            try
            {
                if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
                {
                    CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                    PrusaCompositeStatus status = await breaker.ExecuteAsync(
                        async ct => await _prusa.GetCompositeStatusAsync(
                            p.ServerUrl,
                            p.ApiKey,
                            ct
                        ), 
                        fastTimeoutCts.Token
                    );
                    return await _prusa.CreatePrinterDtoAsync(p, status, ct);
                }
                // ... other backends
            }
            catch
            {
                // Return offline DTO on error
                return CreateOfflinePrinterDto(p);
            }
        })
    );
    
    return dtos;
}
```

**Timeout Strategy**: 1-second timeout for bulk operations (faster fail)

---

### 3. PrusaLink Client Layer

#### **Interface: IPrusaLinkClient** (`src/api/Services/Interfaces/IPrusaLinkClient.cs`)

```csharp
public interface IPrusaLinkClient
{
    // Get comprehensive status (combines /api/status + /api/job)
    Task<PrusaCompositeStatus> GetCompositeStatusAsync(
        string baseUrl, 
        string? apiKey, 
        CancellationToken ct = default
    );

    // Get basic online/state info only
    Task<PrusaStatus> GetStatusAsync(
        string baseUrl, 
        string? apiKey, 
        CancellationToken ct = default
    );

    // Get active job info
    Task<PrusaJob?> GetJobAsync(
        string baseUrl, 
        string? apiKey, 
        CancellationToken ct = default
    );

    // Camera URLs
    Task<string?> GetCameraSnapshotUrlAsync(
        string baseUrl, 
        int? frontendPort = null, 
        CancellationToken ct = default
    );

    Task<string?> GetCameraStreamUrlAsync(
        string baseUrl, 
        int? frontendPort = null, 
        CancellationToken ct = default
    );

    // File operations
    Task<bool> UploadGcodeAsync(...);
    Task<bool> StartPrintAsync(...);
    // ... etc
}
```

#### **Implementation: PrusaLinkClient** (`src/api/Services/PrusaLinkClient.cs`)

```csharp
public class PrusaLinkClient : PrinterClientBase, IPrusaLinkClient
{
    private readonly PrusaLinkApiClient _apiClient;

    public async Task<PrusaCompositeStatus> GetCompositeStatusAsync(
        string baseUrl, 
        string? apiKey, 
        CancellationToken ct = default)
    {
        try
        {
            // Call PrusaLink /api/status endpoint
            StatusInfo? status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);
            
            // Call PrusaLink /api/job endpoint
            Job? job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);

            return new PrusaCompositeStatus(
                IsOnline: status?.Printer != null,
                State: status?.Printer?.State,
                Progress: job?.Progress,
                JobName: job?.File?.Name,
                ThumbnailUrl: null,      // Not available from API
                CameraStreamUrl: null,   // Would need camera config
                CameraSnapshotUrl: null  // Would need camera config
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get composite status from {BaseUrl}", baseUrl);
            return new PrusaCompositeStatus(false, null, null, null, null, null, null);
        }
    }

    public async Task<PrinterDto> CreatePrinterDtoAsync(
        Printer printer,
        PrusaCompositeStatus status,
        CancellationToken ct = default)
    {
        // Get camera URLs
        string? cameraSnapshotUrl = await GetCameraSnapshotUrlAsync(
            printer.ServerUrl, 
            printer.FrontendPort, 
            ct
        );
        string? cameraStreamUrl = await GetCameraStreamUrlAsync(
            printer.ServerUrl, 
            printer.FrontendPort, 
            ct
        );

        return new PrinterDto(
            Id: printer.Id,
            Name: printer.Name,
            ServerUrl: printer.ServerUrl,
            Notes: printer.Notes,
            IsOnline: status.IsOnline,
            State: status.State,
            ManufacturerName: printer.Manufacturer?.Name,
            ModelName: printer.Model?.Name,
            Progress: status.Progress,
            JobName: status.JobName,
            ThumbnailUrl: status.ThumbnailUrl,
            CameraStreamUrl: cameraStreamUrl,
            CameraSnapshotUrl: cameraSnapshotUrl,
            Backend: PrinterBackend.PrusaLink,
            ApiKey: printer.ApiKey,
            OriginalServerUrl: printer.OriginalServerUrl,
            IpAddress: printer.IpAddress,
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort
        );
    }

    public Task<string?> GetCameraSnapshotUrlAsync(
        string baseUrl, 
        int? frontendPort = null, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return Task.FromResult<string?>(null);

            Uri baseUri = new(baseUrl.TrimEnd('/'));
            int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);

            UriBuilder builder = new(baseUri)
            {
                Port = port,
                Path = "/webcam/?action=snapshot",
                Query = null
            };

            return Task.FromResult<string?>(builder.Uri.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error building camera snapshot URL");
            return Task.FromResult<string?>(null);
        }
    }
}
```

#### **HTTP Client: PrusaLinkApiClient** (`src/api/Services/PrusaLinkApiClient.cs`)

Makes actual HTTP requests to PrusaLink API.

```csharp
public class PrusaLinkApiClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public async Task<StatusInfo> GetStatusAsync(
        string baseUrl, 
        string? apiKey = null, 
        CancellationToken ct = default)
    {
        // Calls: GET {baseUrl}/api/status
        // Returns: { printer: { state: "Printing", ... }, ... }
        string url = new Uri(EnsureBaseUri(baseUrl), "api/status").ToString();
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, apiKey);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            _ = response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<StatusInfo>(json, _jsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug($"PrusaLink API call failed for {url}: {ex.Message}");
            throw;
        }
    }

    public async Task<Job> GetJobAsync(
        string baseUrl, 
        string? apiKey = null, 
        CancellationToken ct = default)
    {
        // Calls: GET {baseUrl}/api/job
        // Returns: { state: "Printing", progress: 0.45, file: { name: "print.gcode" }, ... }
        string url = new Uri(EnsureBaseUri(baseUrl), "api/job").ToString();
        // Similar implementation...
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, string? apiKey)
    {
        HttpRequestMessage request = new(method, url);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }
        return request;
    }
}
```

**PrusaLink API Endpoints Called**:
- `GET /api/status` - Printer state and info
- `GET /api/job` - Active print job details
- `GET /webcam/?action=snapshot` - Camera snapshot (optional)

---

### 4. SignalR Hub (for Real-Time Updates)

**File**: `src/api/Hubs/PrinterHub.cs`

```csharp
public class PrinterHub : Hub
{
    // Methods for discovery progress/printers found during discovery
    public async Task BroadcastDiscoveryProgressAsync(DiscoveryProgressDto progress)
    {
        await Clients.Group($"discovery-{progress.SessionId}")
            .SendAsync("discoveryprogress", progress);
    }

    public async Task BroadcastDiscoveryPrinterFoundAsync(DiscoveryPrinterFoundDto found)
    {
        await Clients.Group($"discovery-{found.SessionId}")
            .SendAsync("discoveryprinterfound", found);
    }

    public async Task BroadcastDiscoveryCompletedAsync(DiscoveryCompletedDto completed)
    {
        await Clients.Group($"discovery-{completed.SessionId}")
            .SendAsync("discoverycompleted", completed);
    }
}
```

**⚠️ Important**: The PrinterHub shown above handles **discovery events only**, NOT real-time printer status updates. 

Real-time status updates (`"printerupdated"` event) are sent by `MoonrakerSubscriptionService` for Moonraker printers only:
```csharp
// From MoonrakerSubscriptionService.cs
await hub!.Clients.All.SendAsync("printerupdated", update, ct);
```

---

### 5. Background Service: MoonrakerSubscriptionService

**File**: `src/api/Services/MoonrakerSubscriptionService.cs`

This service manages **continuous real-time subscriptions for Moonraker printers only**.

```csharp
public sealed class MoonrakerSubscriptionService : IHostedService, IDisposable
{
    private readonly IHubContext<PrinterHub> hub;
    private readonly ConcurrentDictionary<Guid, Task> _loops = new();

    public async Task EnumerateAndStartSubscriptionsAsync(CancellationToken ct)
    {
        // From line 194-197:
        // "Note: Only Moonraker supports real-time WebSocket subscriptions
        //  PrusaLink and SDCP are polled via HTTP on-demand, not continuously"
        
        List<Printer> allPrinters = await printersRepo.GetByBackendAsync(
            PrinterBackend.Moonraker,  // ← Only Moonraker!
            ct
        );
        
        List<Printer> enabledPrinters = allPrinters
            .Where(p => p.IsEnabled)
            .ToList();

        foreach (Printer? p in enabledPrinters)
        {
            _ = _loops.GetOrAdd(p.Id, _ => Task.Run(
                () => SubscribePrinterLoopAsync(p, ct), 
                ct
            ));
        }
    }

    private async Task ProcessStatusUpdateAsync(...)
    {
        // ... parse WebSocket message ...
        
        // Broadcast via SignalR
        await hub.Clients.All.SendAsync("printerupdated", statusUpdate, ct);
    }
}
```

**Critical Finding** (Line 194-197):
```
// Only subscribe to ENABLED Moonraker-backed printers
// Note: Only Moonraker supports real-time WebSocket subscriptions
// PrusaLink and SDCP are polled via HTTP on-demand, not continuously
```

---

## Status Update Mechanisms

### For Moonraker Printers (WebSocket Subscription)

```
MoonrakerSubscriptionService (background)
  ↓
  WebSocket connection to printer
  ↓
  Continuous status updates
  ↓
  SignalR Hub: Clients.All.SendAsync("printerupdated", ...)
  ↓
  React Client receives real-time updates
```

### For PrusaLink Printers (HTTP On-Demand Polling)

```
React Client requests status
  ↓
HTTP GET /api/printers/{id}/status
  ↓
PrintersController.GetStatusAsync()
  ↓
PrintersService.GetStatusDtoAsync()
  ↓
PrusaLinkClient.GetCompositeStatusAsync()
  ↓
PrusaLinkApiClient (HTTP requests)
  ├─ GET /api/status
  └─ GET /api/job
  ↓
HTTP Response (JSON)
  ↓
React Client receives status
```

**Key Difference**: PrusaLink has NO continuous subscription. Status is fetched on-demand when the client requests it.

---

## Error Handling & Logging

### Circuit Breaker Pattern

Each backend printer uses a per-printer circuit breaker:

```csharp
CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");

PrusaCompositeStatus status = await breaker.ExecuteAsync(
    async ct => await _prusa.GetCompositeStatusAsync(...),
    statusCts.Token
);
```

**Benefits**:
- Prevents cascading failures if printer is down
- Exponential backoff after failures
- Per-printer isolation (one printer's failure doesn't affect others)

### Timeout Strategy

| Operation | Timeout | Location |
|-----------|---------|----------|
| Single printer status | 3 seconds | `GetStatusDtoAsync` |
| Bulk printer statuses | 1 second per printer | `GetAllWithStatusDtosAsync` |
| API call | Default HttpClient timeout | `PrusaLinkApiClient` |

### Logging

**PrusaLinkClient**:
```csharp
_logger?.LogError(ex, "Failed to get composite status from {BaseUrl}", baseUrl);
_logger?.LogError(ex, "Failed to get status from {BaseUrl}", baseUrl);
_logger?.LogError(ex, "Failed to get job from {BaseUrl}", baseUrl);
```

**PrintersService**:
```csharp
_logger.LogWarning($"Status timeout for printer {p.Id}");
_logger.LogWarning($"Error getting status for printer {p.Id}: {ex.Message}");
```

**PrusaLinkApiClient**:
```csharp
_logger.LogDebug($"PrusaLink API call failed for {url}: {ex.Message}");
_logger.LogDebug($"PrusaLink API deserialization failed for {url}: {ex.Message}");
```

---

## Data Models

### PrusaCompositeStatus

```csharp
public record PrusaCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl
);
```

**Sources**:
- `IsOnline`: From `status?.Printer != null`
- `State`: From `status?.Printer?.State`
- `Progress`: From `job?.Progress`
- `JobName`: From `job?.File?.Name`
- `ThumbnailUrl`: Not available from PrusaLink API (returns null)
- `CameraStreamUrl`: Constructed from baseUrl + port
- `CameraSnapshotUrl`: Constructed from baseUrl + port

### PrinterStatusDto

```csharp
public record PrinterStatusDto(
    Guid Id,
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl,
    // Optional positioning/temperature fields (Moonraker/SDCP only)
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null,
    PrinterSpoolInfoDto? SpoolInfo = null
);
```

### PrinterDto

```csharp
public record PrinterDto(
    Guid Id,
    string Name,
    string ServerUrl,
    string? Notes,
    bool IsOnline,
    string? State,
    string? ManufacturerName,
    string? ModelName,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl,
    PrinterBackend Backend,
    string? ApiKey,
    string? OriginalServerUrl,
    string? IpAddress,
    int? BackendPort,
    int? FrontendPort
);
```

---

## Configuration & URL Handling

### Printer Database Model

```csharp
public class Printer
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string ServerUrl { get; set; }           // e.g., "http://192.168.1.100"
    public string? OriginalServerUrl { get; set; }  // Original hostname before resolution
    public string? IpAddress { get; set; }          // Resolved IP
    public int? BackendPort { get; set; }           // Printer backend port (optional)
    public int? FrontendPort { get; set; }          // Frontend/camera port (optional)
    public PrinterBackend Backend { get; set; }     // Enum: Moonraker, PrusaLink, SDCP, OctoPrint
    public string? ApiKey { get; set; }             // API authentication key
    public string? Notes { get; set; }
    public bool IsEnabled { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public Model? Model { get; set; }
}
```

### Camera URL Construction

```csharp
public Task<string?> GetCameraSnapshotUrlAsync(
    string baseUrl, 
    int? frontendPort = null, 
    CancellationToken ct = default)
{
    // baseUrl example: "http://192.168.1.100"
    // frontendPort example: 80
    
    Uri baseUri = new(baseUrl.TrimEnd('/'));  // http://192.168.1.100
    int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);

    UriBuilder builder = new(baseUri)
    {
        Port = port,
        Path = "/webcam/?action=snapshot",
        Query = null
    };

    // Result: http://192.168.1.100:80/webcam/?action=snapshot
    return Task.FromResult<string?>(builder.Uri.ToString());
}
```

**Camera Endpoints**:
- Snapshot: `/webcam/?action=snapshot`
- Stream: `/webcam/?action=stream` (MJPEG)

---

## Comparison: Moonraker vs PrusaLink vs SDCP

| Feature | Moonraker | PrusaLink | SDCP |
|---------|-----------|-----------|------|
| **Status Subscription** | WebSocket (real-time) | HTTP polling (on-demand) | HTTP polling (on-demand) |
| **Background Service** | MoonrakerSubscriptionService | None | None |
| **SignalR Updates** | Continuous (`"printerupdated"`) | None (client-driven) | None (client-driven) |
| **Status Endpoints** | WebSocket `/websocket` + JSON-RPC | `/api/status`, `/api/job` | Proprietary endpoints |
| **Timeout** | Heartbeat-based | 3 seconds (status), 1 second (bulk) | 3 seconds (status), 1 second (bulk) |
| **Circuit Breaker** | Per-printer | Per-printer | Per-printer |
| **Camera Support** | Via metadata | URL constructed | Proprietary |
| **File Management** | Via Moonraker API | Via PrusaLink API | Via SDCP API |
| **Job History** | `/history/list` endpoint | N/A | N/A |

---

## Request Flow Example: PrusaLink

**Scenario**: User opens dashboard and views a Prusa printer's status

### Step 1: React Client Makes Request

```
GET http://localhost:5245/api/printers/a1b2c3d4-e5f6-7890-abcd-ef1234567890/status
```

### Step 2: Controller Receives Request

```csharp
// PrintersController.GetStatusAsync()
PrinterStatusDto dto = await _printersService.GetStatusDtoAsync(id, ct);
```

### Step 3: Service Retrieves Printer from Database

```csharp
// PrintersService.GetStatusDtoAsync()
Printer? p = await _repo.FindByIdAsync(id, ct);
// p.Backend = PrusaLink
// p.ServerUrl = "http://192.168.1.100"
// p.ApiKey = "api-key-here"
```

### Step 4: Service Calls PrusaLink Client

```csharp
CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
PrusaCompositeStatus status = await breaker.ExecuteAsync(
    async ct => await _prusa.GetCompositeStatusAsync(
        "http://192.168.1.100",
        "api-key-here",
        ct
    ), 
    statusCts.Token  // 3-second timeout
);
```

### Step 5: PrusaLink Client Makes HTTP Requests

```
HTTP GET http://192.168.1.100/api/status
Headers: X-Api-Key: api-key-here

HTTP GET http://192.168.1.100/api/job
Headers: X-Api-Key: api-key-here
```

### Step 6: PrusaLink API Responses

**Response 1 - /api/status**:
```json
{
  "printer": {
    "state": "Printing",
    "temperature": 220,
    "target_temperature": 220
  }
}
```

**Response 2 - /api/job**:
```json
{
  "state": "Printing",
  "progress": 0.45,
  "file": {
    "name": "benchy.gcode",
    "size": 1234567
  }
}
```

### Step 7: Client Aggregates Status

```csharp
return new PrusaCompositeStatus(
    IsOnline: true,           // status?.Printer != null
    State: "Printing",        // status?.Printer?.State
    Progress: 0.45,           // job?.Progress
    JobName: "benchy.gcode",  // job?.File?.Name
    ThumbnailUrl: null,
    CameraStreamUrl: "http://192.168.1.100:80/webcam/?action=stream",
    CameraSnapshotUrl: "http://192.168.1.100:80/webcam/?action=snapshot"
);
```

### Step 8: Service Returns DTO to Controller

```csharp
return new PrinterStatusDto(
    Id: a1b2c3d4-e5f6-7890-abcd-ef1234567890,
    IsOnline: true,
    State: "Printing",
    Progress: 0.45,
    JobName: "benchy.gcode",
    CameraStreamUrl: "http://192.168.1.100:80/webcam/?action=stream",
    CameraSnapshotUrl: "http://192.168.1.100:80/webcam/?action=snapshot"
);
```

### Step 9: Controller Returns HTTP Response

```
HTTP 200 OK
Content-Type: application/json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "isOnline": true,
  "state": "Printing",
  "progress": 0.45,
  "jobName": "benchy.gcode",
  "cameraStreamUrl": "http://192.168.1.100:80/webcam/?action=stream",
  "cameraSnapshotUrl": "http://192.168.1.100:80/webcam/?action=snapshot"
}
```

### Step 10: React Client Receives Response

React state is updated with the new status information and component re-renders.

---

## File Paths & References

| Component | File Path | Key Methods/Classes |
|-----------|-----------|---------------------|
| Controller | `src/api/Controllers/PrintersController.cs` | `GetStatusAsync()`, `GetAsync()`, `GetDetailsAsync()` |
| Service | `src/api/Services/Printers/PrintersService.cs` | `GetStatusDtoAsync()`, `GetPrinterDtoAsync()`, `GetAllWithStatusDtosAsync()` |
| Client Interface | `src/api/Services/Interfaces/IPrusaLinkClient.cs` | `GetCompositeStatusAsync()`, `GetStatusAsync()`, `GetJobAsync()` |
| Client Impl | `src/api/Services/PrusaLinkClient.cs` | `PrusaLinkClient` class, `CreatePrinterDtoAsync()` |
| HTTP Client | `src/api/Services/PrusaLinkApiClient.cs` | `GetStatusAsync()`, `GetJobAsync()`, `CreateRequest()` |
| SignalR Hub | `src/api/Hubs/PrinterHub.cs` | `BroadcastDiscoveryProgressAsync()`, discovery events |
| Background Service | `src/api/Services/MoonrakerSubscriptionService.cs` | Moonraker only - NOT used for PrusaLink |
| Program Setup | `src/api/Program.cs` | Line 655: `app.MapHub<PrinterHub>("/hubs/printers")` |

---

## Summary: Key Insights

1. **PrusaLink Status is Request-Driven**: Unlike Moonraker, there is no background service continuously polling PrusaLink. Status is fetched on-demand via HTTP when the client requests it.

2. **No Real-Time Subscriptions**: PrusaLink does not support WebSocket subscriptions. All status fetches are synchronous HTTP GET requests with a 3-second timeout.

3. **Per-Printer Circuit Breaker**: Each PrusaLink printer has its own circuit breaker to prevent cascading failures and provide isolated error handling.

4. **Client-Side Polling**: For real-time updates on PrusaLink, the React client must implement its own polling mechanism (e.g., polling `/api/printers/{id}/status` every N seconds).

5. **Limited Data**: PrusaLink API provides less data than Moonraker. Thumbnail URLs are not available from the API (always null). Camera URLs are constructed from the base URL and frontend port.

6. **Timeout Behavior**: Status requests timeout after 3 seconds. Bulk operations (getting all printers) use a 1-second timeout per printer. Timeouts return "offline" status.

7. **Error Resilience**: On any error (timeout, HTTP error, network error), the service logs the error and returns an "offline" DTO. This prevents UI crashes and shows a degraded status.

8. **Separate Discovery Hub**: The PrinterHub only handles discovery events (when adding new printers). Real-time printer status updates are NOT sent via SignalR for PrusaLink; they are only sent for Moonraker.

---

## Potential Issues & Limitations

1. **No Real-Time Updates**: PrusaLink printers won't show live status updates on the dashboard unless the client actively polls. This differs significantly from Moonraker's real-time behavior.

2. **Camera Support Gap**: Thumbnail URLs from active jobs are not available from PrusaLink API. Only webcam snapshots/streams (if configured) can be displayed.

3. **Circuit Breaker Timeout**: If a printer is temporarily unreachable, the circuit breaker may prevent retries for up to ~3 seconds, during which status is reported as "offline."

4. **Single API Key**: All requests to a PrusaLink printer use the same API key from the database. There's no per-client or session-based authentication.

5. **Limited Capabilities Discovery**: PrusaLink discovery is more limited than Moonraker. The `DiscoverFromPrusaLinkAsync()` method only retrieves basic info and doesn't populate full capabilities.

---

## Recommendations for Future Enhancement

1. **Implement Client-Side Polling**: Add logic in React to periodically fetch PrusaLink status (e.g., every 5 seconds when printer is online).

2. **SignalR Polling Wrapper**: Consider adding a background service that periodically polls PrusaLink printers and broadcasts updates via SignalR, similar to Moonraker's subscription service.

3. **WebSocket Support**: If PrusaLink gains WebSocket support in future versions, implement a similar subscription mechanism to Moonraker.

4. **Thumbnail Fallback**: Investigate if PrusaLink exposes thumbnail data via a different endpoint or protocol.

5. **Per-Request Timeout Configuration**: Allow clients to specify timeout preferences (e.g., aggressive timeout for dashboard, lenient timeout for detailed status page).

