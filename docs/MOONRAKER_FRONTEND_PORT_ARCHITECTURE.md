# Moonraker Frontend Port Architecture

## Overview

This document explains the multi-port architecture for Moonraker/Klipper printers, where API requests are routed through the frontend port instead of the hardcoded Moonraker port (7125).

## Problem Statement

Previously, all Moonraker API calls were hardcoded to use port 7125. However, for proper network access:
- **Discovery probes** need port 7125 (the actual Moonraker API port)
- **All other API calls** should route through the FrontendPort (default 80, user-configurable)
- Moonraker automatically routes frontend requests to port 7125 internally

## Architecture

### Three Port Concepts

1. **ServerUrl** (host + scheme only)
   - Stores: `http://192.168.1.50` or `https://printer.local`
   - Does NOT store embedded port
   - Used as the base for all URL construction

2. **BackendPort** (optional, Moonraker always 7125)
   - For Moonraker: Always 7125 (not needed in stored form, used implicitly)
   - For PrusaLink: 8080, 8443, etc.
   - For SDCP/Elegoo: varies

3. **FrontendPort** (user-configurable)
   - Default: 80 (HTTP) or 443 (HTTPS)
   - User-configurable for special cases:
     - Phrozen Arco uses 8080 or 8808
     - Custom installations may vary
   - Used for:
     - All Moonraker API requests (they route to 7125 internally)
     - Camera streams and snapshots
     - Web UI access

## Implementation Changes

### Discovery Phase (Network Discovery)

**File**: `MoonrakerDiscoveryProbe.cs`

```csharp
protected override int[] Ports => new[] { 7125, 80 };
```

- Probes port **7125 first** (the actual Moonraker API port)
- Then probes port **80** (for alternate installations)
- Uses hardcoded ports for discovery, does NOT use database FrontendPort

### API Request Phase (After Discovery)

**File**: `PrintersService.cs`

New helper method:
```csharp
private static string BuildMoonrakerUrl(string serverUrl, int? frontendPort)
{
    // Constructs URL with FrontendPort (default 80/443)
    // Example: http://192.168.1.50:8080 for Phrozen Arco
}
```

**All API calls now use this pattern:**
```csharp
string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
await _moon.GetCompositeStatusAsync(moonrakerUrl, ct);
```

### Methods Updated (15+ total)

History API:
- `GetHistoryListAsync()`
- `GetHistoryJobAsync()`
- `GetHistoryTotalsAsync()`
- `DeleteHistoryJobAsync()`

Status & Movement:
- `GetCompositeStatusAsync()` (3 callers)
- `SendHomeAsync()`
- `HomeXYAsync()`
- `HomeZAsync()`

Temperature & Motion:
- `SetTempsAsync()`
- `MoveAsync()`
- `MoveToAsync()`

Print Control:
- `PauseAsync()`
- `ResumeAsync()`
- `EmergencyStopAsync()`

File Management:
- `UploadGcodeAsync()`
- `GetFileListAsync()`

Other:
- `GetCameraSnapshotAsync()` (direct Moonraker method)
- `FirmwareRestartAsync()`
- `GetPrintJobStatusAsync()`
- `GetSpoolInfoAsync()` (spooler API calls)

### Camera URL Generation

**File**: `PrintersService.cs` - `GenerateStaticCameraStreamUrl()` and `GenerateStaticCameraSnapshotUrl()`

Already correctly using FrontendPort:
```csharp
int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);
```

## Data Flow

### Discovery Flow (Initial Setup)
```
Network Discovery
  ↓
Probe port 7125 (MoonrakerDiscoveryProbe)
  ↓
Printer found + discovered
  ↓
Store: ServerUrl, FrontendPort (if different from 80)
```

### API Flow (Ongoing Operation)
```
PrintersService requests printer data
  ↓
Build moonrakerUrl = BuildMoonrakerUrl(ServerUrl, FrontendPort)
  ↓
Pass moonrakerUrl to MoonrakerClient
  ↓
MoonrakerClient makes HTTP request to port 80/8080/etc.
  ↓
Moonraker internally routes to port 7125
  ↓
Response returned to client
```

## Benefits

1. **Flexible Frontend Port**: Supports printers with custom web UI ports (Phrozen Arco, etc.)
2. **Standard API Access**: All API requests go through normal web server port
3. **Backward Compatible**: Default 80 works for standard setups
4. **Clean Separation**: Discovery uses port 7125, API uses FrontendPort
5. **User Configurable**: Port can be set during printer setup

## Database Fields

**Printer Entity** (src/infra/Domain/Entities.cs)

```csharp
public string ServerUrl { get; set; }          // "http://192.168.1.50"
public int? BackendPort { get; set; }          // Not used for Moonraker (always 7125)
public int? FrontendPort { get; set; }         // 80 (default), 8080, 8808, etc.
```

## Example Scenarios

### Scenario 1: Standard Setup (Port 80)

User adds: `http://192.168.1.50`

```
ServerUrl: http://192.168.1.50
FrontendPort: null (uses default 80)
  ↓
API requests go to: http://192.168.1.50:80
  ↓
Moonraker routes to port 7125 internally
```

### Scenario 2: Phrozen Arco (Port 8808)

User adds: `http://192.168.1.50` with FrontendPort set to 8808

```
ServerUrl: http://192.168.1.50
FrontendPort: 8808
  ↓
API requests go to: http://192.168.1.50:8808
  ↓
Moonraker routes to port 7125 internally
```

### Scenario 3: HTTPS with Custom Port

User adds: `https://printer.local` with FrontendPort 8443

```
ServerUrl: https://printer.local
FrontendPort: 8443
  ↓
API requests go to: https://printer.local:8443
  ↓
Moonraker routes to port 7125 internally
```

## Migration Notes

For existing installations:
1. ServerUrl already does not contain embedded ports (handled by NormalizeServerUrl)
2. BackendPort field exists but unused for Moonraker
3. FrontendPort field exists but defaults to null (safe default of 80)
4. No migration needed - existing printers continue working

## Testing

### Unit Tests
All PrintersService API methods now tested with BuildMoonrakerUrl:
- Verify correct port is used
- Verify FrontendPort null defaults to 80
- Verify HTTPS scheme defaults to 443

### Integration Tests
- Discovery probe still uses port 7125
- API calls successfully route through FrontendPort
- Camera URLs work with FrontendPort

## Related Documentation

- `CAMERA_URL_ARCHITECTURE.md` - Camera port configuration
- `DISCOVERY_CANCELLATION_TOKEN_DESIGN.md` - Discovery process
- `MoonrakerClient.cs` - HTTP client implementation
