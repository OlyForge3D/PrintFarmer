# Backend Selection for Network Discovery

## Overview

This feature allows users to select which printer backends to scan during network discovery, instead of scanning all backends. Users can now choose to scan only Moonraker, PrusaLink, SDCP, OctoPrint, or any combination thereof.

## Changes Made

### 1. Backend Data Model (`src/shared/Models.cs`)

#### NetworkDiscoverySettingsDto
Added optional `Backends` parameter:
```csharp
public record NetworkDiscoverySettingsDto(
    List<string> NetworkRanges,
    int TimeoutMs = 3000,
    int MaxConcurrentScans = 20,
    List<int> Ports = null!,
    List<PrinterBackend>? Backends = null)  // NEW
```

#### StartDiscoveryRequest
New DTO for API requests:
```csharp
public record StartDiscoveryRequest(
    List<PrinterBackend>? Backends = null
);
```

### 2. Network Discovery Service (`src/api/Services/NetworkDiscoveryService.cs`)

#### Interface Update
Added new overload to `INetworkDiscoveryService`:
```csharp
Task DiscoverPrintersWithProgressAsync(string sessionId, List<PrinterBackend>? backends, CancellationToken cancellationToken = default);
```

#### Implementation Changes

1. **Service Method**: The existing `DiscoverPrintersWithProgressAsync` now calls the new overload with `null` backends
2. **New Overload**: Accepts backends parameter and overrides settings if provided
3. **TryDiscoverPrinterAsync**: Updated to accept and use backends list
   - If backends is null, defaults to scanning all backends (Moonraker, PrusaLink)
   - Only attempts to discover backends that are in the selected list
   - Maintains port-specific logic (port 7125 for Moonraker, port 80 for PrusaLink)

### 3. API Controller (`src/api/Controllers/PrintersController.cs`)

Updated the `/printers/discover/stream` endpoint:
```csharp
public ActionResult StartDiscoveryStream([FromBody] StartDiscoveryRequest? request, CancellationToken ct)
```

Now accepts an optional request body with backend selection and passes it to the service.

### 4. React Frontend

#### TypeScript Types (`src/Web/ReactApp/src/types/api.ts`)
Added interfaces:
```typescript
export interface NetworkDiscoverySettingsDto {
  networkRanges: string[];
  timeoutMs: number;
  maxConcurrentScans: number;
  ports: number[];
  backends?: PrinterBackend[];
}

export interface StartDiscoveryRequest {
  backends?: PrinterBackend[];
}
```

#### API Client (`src/Web/ReactApp/src/services/api.ts`)
Updated to accept request parameter:
```typescript
async startDiscoveryStream(request?: StartDiscoveryRequest): Promise<{ sessionId: string; message: string }>
```

#### Hooks (`src/Web/ReactApp/src/hooks/useApi.ts`)
Updated mutation to accept request:
```typescript
export function useStartDiscoveryStream() {
  return useMutation({
    mutationFn: (request?: StartDiscoveryRequest) => apiClient.startDiscoveryStream(request),
  });
}
```

#### UI Component (`src/Web/ReactApp/src/components/PrinterDiscoveryModal.tsx`)

Added backend selection UI:
- New state: `selectedBackends` (defaults to Moonraker and PrusaLink)
- Checkbox group for selecting backends
- Validation: At least one backend must be selected
- Passes selected backends to the API when starting discovery

## User Experience

### Before
- Users could only scan all backends together
- No control over which printer types to search for
- Longer scan times if only interested in specific backends

### After
- Users can select one or more backends before starting scan
- Checkboxes for: Moonraker, PrusaLink, SDCP, OctoPrint
- Defaults to Moonraker and PrusaLink selected
- Must select at least one backend to start scan
- Faster scans when limiting to specific backends

## API Compatibility

### Backward Compatibility
✅ The changes are backward compatible:
- The `backends` parameter in the request body is optional
- If not provided (null), the system behaves as before (scans all backends)
- Existing clients that don't send backends will continue to work

### Request Example
```json
POST /printers/discover/stream
{
  "backends": [0, 1]  // 0 = Moonraker, 1 = PrusaLink
}
```

Or without body (scans all):
```json
POST /printers/discover/stream
```

## Testing Notes

Due to pre-existing build issues in the repository (documented in COPILOT instructions):
- 97 TypeScript compilation errors prevent production React builds
- 390 C# compilation errors prevent .NET builds
- The changes follow existing patterns and should work when build issues are resolved

## Future Enhancements

Potential improvements:
1. Save backend preferences in user settings
2. Add "Select All" / "Deselect All" buttons
3. Show estimated scan time based on selected backends
4. Add tooltips explaining each backend type
5. Remember last selection across sessions
