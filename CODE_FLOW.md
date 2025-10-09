# Backend Selection - Code Flow Documentation

## Request Flow

This document explains how a backend selection request flows through the system.

### 1. User Interaction (Frontend)

**File**: `src/Web/ReactApp/src/components/PrinterDiscoveryModal.tsx`

```typescript
// User clicks checkboxes to select backends
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

### 2. API Client (Frontend)

**File**: `src/Web/ReactApp/src/services/api.ts`

```typescript
async startDiscoveryStream(request?: StartDiscoveryRequest): Promise<{ sessionId: string; message: string }> {
  const response = await this.client.post<{ sessionId: string; message: string }>(
    '/printers/discover/stream', 
    request || {}
  );
  return response.data;
}
```

### 3. API Controller (Backend)

**File**: `src/api/Controllers/PrintersController.cs`

```csharp
[HttpPost("discover/stream")]
public ActionResult StartDiscoveryStream(
    [FromBody] StartDiscoveryRequest? request, 
    CancellationToken ct)
{
    string sessionId = Guid.NewGuid().ToString();
    
    _ = Task.Run(async () =>
    {
        // Pass backends to service
        await networkDiscovery.DiscoverPrintersWithProgressAsync(
            sessionId, 
            request?.Backends,  // <-- Backend list passed here
            timeoutCts.Token
        );
    }, ct);
    
    return Ok(new { sessionId, message = "Discovery started..." });
}
```

### 4. Network Discovery Service

**File**: `src/api/Services/NetworkDiscoveryService.cs`

#### Step 4a: Service Entry Point

```csharp
public async Task DiscoverPrintersWithProgressAsync(
    string sessionId, 
    List<PrinterBackend>? backends,  // <-- Backends from request
    CancellationToken cancellationToken = default)
{
    NetworkDiscoverySettingsDto settings = _settingsService.GetSettings();
    
    // Override backends if provided in the request
    if (backends != null && backends.Count > 0)
    {
        settings = settings with { Backends = backends };
    }
    
    // Continue with network scanning...
}
```

#### Step 4b: Host Scanning

```csharp
private async Task<DiscoveredPrinterDto?> ScanHostAsync(
    string ipAddress, 
    NetworkDiscoverySettingsDto settings, 
    HashSet<string> existingServerUrls, 
    CancellationToken cancellationToken)
{
    foreach (int port in settings.Ports)
    {
        DiscoveredPrinterDto? discovered = await TryDiscoverPrinterAsync(
            ipAddress, 
            port, 
            settings.TimeoutMs, 
            settings.Backends,  // <-- Pass backends to discovery logic
            cancellationToken
        );
        
        if (discovered != null)
        {
            return discovered;
        }
    }
    return null;
}
```

#### Step 4c: Backend-Specific Discovery

```csharp
private async Task<DiscoveredPrinterDto?> TryDiscoverPrinterAsync(
    string ipAddress, 
    int port, 
    int timeoutMs, 
    List<PrinterBackend>? backends,  // <-- Backends to scan
    CancellationToken cancellationToken)
{
    // Default to all backends if none specified
    List<PrinterBackend> backendsToScan = backends ?? 
        [PrinterBackend.Moonraker, PrinterBackend.PrusaLink];
    
    // Try each selected backend
    foreach (PrinterBackend backend in backendsToScan)
    {
        if (backend == PrinterBackend.Moonraker && port == 7125)
        {
            PrinterInfo? moonrakerInfo = await TryGetMoonrakerInfoAsync(...);
            if (moonrakerInfo != null)
            {
                return CreateDiscoveredPrinter(ipAddress, port, 
                    PrinterBackend.Moonraker, moonrakerInfo);
            }
        }
        else if (backend == PrinterBackend.PrusaLink && port == 80)
        {
            PrinterInfo? prusaInfo = await TryGetPrusaLinkInfoAsync(...);
            if (prusaInfo != null)
            {
                return CreateDiscoveredPrinter(ipAddress, port, 
                    PrinterBackend.PrusaLink, prusaInfo);
            }
        }
        // Additional backends can be added here...
    }
    
    return null;
}
```

## Data Flow Diagram

```
┌─────────────────┐
│  User selects   │
│   backends in   │
│   checkboxes    │
└────────┬────────┘
         │
         ▼
┌─────────────────────────┐
│ handleStartDiscovery()  │
│ - Collects selected     │
│   backends from state   │
│ - Creates request obj   │
└────────┬────────────────┘
         │
         ▼
┌─────────────────────────┐
│ startDiscoveryStream()  │
│ - Sends POST request    │
│ - Body: { backends: [] }│
└────────┬────────────────┘
         │
         ▼
┌──────────────────────────────┐
│ PrintersController           │
│ .StartDiscoveryStream()      │
│ - Receives request body      │
│ - Extracts backends list     │
│ - Generates session ID       │
│ - Starts background task     │
└────────┬─────────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│ NetworkDiscoveryService          │
│ .DiscoverPrintersWithProgressAsync()│
│ - Gets base settings             │
│ - Overrides with request backends│
│ - Starts network scanning        │
└────────┬─────────────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│ For each IP in network range:    │
│ .ScanHostAsync()                 │
│ - Tries each port                │
│ - Passes backends to discovery   │
└────────┬─────────────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│ For each port:                   │
│ .TryDiscoverPrinterAsync()       │
│ - Loops through selected backends│
│ - Only queries selected types    │
│ - Returns first match            │
└────────┬─────────────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│ Backend-specific API calls:      │
│ - TryGetMoonrakerInfoAsync()     │
│ - TryGetPrusaLinkInfoAsync()     │
│ - Returns PrinterInfo or null    │
└────────┬─────────────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│ If printer found:                │
│ .CreateDiscoveredPrinter()       │
│ - Creates DiscoveredPrinterDto   │
│ - Sends via SignalR to frontend  │
└──────────────────────────────────┘
```

## Key Design Decisions

### 1. Optional Parameter with Default Behavior

**Decision**: Make `backends` parameter optional throughout the stack

**Rationale**:
- Maintains backward compatibility
- Defaults to current behavior (scan all) when not specified
- Progressive enhancement - existing clients continue to work

### 2. Override Pattern in Service Layer

**Code**:
```csharp
if (backends != null && backends.Count > 0)
{
    settings = settings with { Backends = backends };
}
```

**Rationale**:
- Settings can come from file or defaults
- Request parameter takes precedence (session-specific)
- Doesn't modify stored settings
- Uses C# record `with` syntax for immutability

### 3. Port-Backend Mapping Preserved

**Code**:
```csharp
if (backend == PrinterBackend.Moonraker && port == 7125)
if (backend == PrinterBackend.PrusaLink && port == 80)
```

**Rationale**:
- Maintains existing port conventions
- Prevents wasteful scanning (e.g., Moonraker on port 80)
- Backend selection filters which ports/backends to try
- Doesn't change fundamental discovery logic

### 4. Frontend Validation

**Code**:
```typescript
disabled={selectedBackends.size === 0}
```

**Rationale**:
- Prevents empty requests
- Immediate user feedback
- Avoids unnecessary API calls
- Better UX than server-side validation only

## Performance Implications

### Before (scanning all backends):
```
For each IP (254 IPs):
  For each port (80, 7125):
    Try Moonraker if port = 7125
    Try PrusaLink if port = 80
    
Total attempts: 254 IPs × 2 ports = 508 attempts
```

### After (e.g., only Moonraker selected):
```
For each IP (254 IPs):
  For each port (7125):
    Try Moonraker if port = 7125
    
Total attempts: 254 IPs × 1 port = 254 attempts
Time saved: ~50% (assuming similar timeout per attempt)
```

### Benefits:
- **Reduced network traffic**: Only scans selected backend ports
- **Faster discovery**: Fewer API calls = faster completion
- **Lower CPU usage**: Less parallel processing
- **Better targeting**: Users only scan for printers they have

## Error Handling

All existing error handling remains intact:

1. **Network errors**: Caught and logged per-host
2. **Timeouts**: Handled with CancellationToken
3. **Invalid requests**: Controller validates request body
4. **SignalR errors**: Isolated from discovery process
5. **Validation**: Frontend prevents invalid selections

## Future Extension Points

To add a new backend (e.g., Duet):

1. Add to `PrinterBackend` enum
2. Add checkbox in UI
3. Add case in `TryDiscoverPrinterAsync`:
   ```csharp
   else if (backend == PrinterBackend.Duet && port == 80)
   {
       PrinterInfo? duetInfo = await TryGetDuetInfoAsync(...);
       if (duetInfo != null)
       {
           return CreateDiscoveredPrinter(...);
       }
   }
   ```
4. Implement `TryGetDuetInfoAsync` method

No changes needed to request flow, DTOs, or controller!
