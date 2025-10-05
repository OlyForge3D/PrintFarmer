# Backend Selection - Quick Reference Guide

## For End Users

### How to Use Backend Selection

1. **Open Printer Discovery Modal**
   - Click "Add Printer" or "Discover Printers" button
   - The discovery modal will open

2. **Select Backends to Scan**
   - You'll see checkboxes for:
     - ☑ **Moonraker** - Klipper-based printers (default selected)
     - ☑ **PrusaLink** - Prusa printers (default selected)  
     - ☐ **SDCP** - Simple Data Communication Protocol
     - ☐ **OctoPrint** - OctoPrint-enabled printers

3. **Customize Your Selection**
   - Check/uncheck boxes based on your printers
   - At least one backend must be selected
   - Selecting fewer backends = faster scans

4. **Start Scanning**
   - Click "Start Network Scan"
   - Only selected backend types will be scanned
   - Progress shows in real-time

### Tips

- **Default Selection**: If you only have Moonraker and PrusaLink printers, you don't need to change anything
- **Faster Scans**: Uncheck backends you don't have to speed up discovery
- **Large Networks**: If scanning takes too long, reduce the number of selected backends
- **Unknown Printer**: If unsure, select all backends to find all compatible printers

### Common Scenarios

#### Scenario 1: Only Klipper/Moonraker Printers
```
Select: ☑ Moonraker
Uncheck: ☐ PrusaLink, ☐ SDCP, ☐ OctoPrint
Result: Fastest scan for Moonraker-only setups
```

#### Scenario 2: Only Prusa Printers
```
Select: ☑ PrusaLink
Uncheck: ☐ Moonraker, ☐ SDCP, ☐ OctoPrint
Result: Scans only for PrusaLink printers
```

#### Scenario 3: Mixed Environment
```
Select: ☑ Moonraker, ☑ PrusaLink, ☑ OctoPrint
Result: Finds all common printer types
```

#### Scenario 4: First Time / Unknown
```
Select: ☑ All backends
Result: Comprehensive scan, finds everything
```

---

## For Developers

### Quick Implementation Reference

#### Frontend - Adding Backend Selection to Modal

```typescript
// State for selected backends
const [selectedBackends, setSelectedBackends] = useState<Set<PrinterBackend>>(
  new Set([PrinterBackend.Moonraker, PrinterBackend.PrusaLink])
);

// Start discovery with selection
const backends = selectedBackends.size > 0 
  ? Array.from(selectedBackends) 
  : undefined;
await startDiscoveryMutation.mutateAsync({ backends });
```

#### Backend - Handling Backend Selection

```csharp
// Controller endpoint
[HttpPost("discover/stream")]
public ActionResult StartDiscoveryStream(
    [FromBody] StartDiscoveryRequest? request, 
    CancellationToken ct)
{
    await networkDiscovery.DiscoverPrintersWithProgressAsync(
        sessionId, 
        request?.Backends,  // Pass selected backends
        ct
    );
}

// Service implementation
if (backends != null && backends.Count > 0)
{
    settings = settings with { Backends = backends };
}
```

### API Examples

#### Request with Backend Selection
```http
POST /printers/discover/stream
Content-Type: application/json

{
  "backends": [0, 1]  // 0=Moonraker, 1=PrusaLink
}
```

#### Request without Selection (defaults to all)
```http
POST /printers/discover/stream
Content-Type: application/json

{}
```

### Backend Enum Values
```
Moonraker  = 0
PrusaLink  = 1
SDCP       = 2
OctoPrint  = 3
```

### Testing

#### Test Backend Selection
```csharp
[Fact]
public void NetworkDiscoverySettings_AcceptsBackends()
{
    var settings = new NetworkDiscoverySettingsDto(
        NetworkRanges: new List<string> { "192.168.1.0/24" },
        Backends: new List<PrinterBackend> { 
            PrinterBackend.Moonraker, 
            PrinterBackend.PrusaLink 
        }
    );
    
    settings.Backends.Should().HaveCount(2);
}
```

### Debugging

#### Check What Backends Were Selected
1. Check browser network tab - POST body to `/printers/discover/stream`
2. Check API logs - look for "Discovery settings: ... Ports=..."
3. Check service logs - "Testing Moonraker at..." or "Testing PrusaLink at..."

#### Common Issues

**Issue**: Scan takes too long
- **Solution**: Reduce number of selected backends

**Issue**: Printer not found
- **Solution**: Ensure correct backend is selected for your printer type

**Issue**: Button disabled
- **Solution**: Select at least one backend

---

## Configuration

### Default Backends (in code)
Location: `PrinterDiscoveryModal.tsx`
```typescript
const [selectedBackends, setSelectedBackends] = useState<Set<PrinterBackend>>(
  new Set([PrinterBackend.Moonraker, PrinterBackend.PrusaLink])  // Defaults
);
```

### Backend Port Mapping
```
Moonraker: Port 7125
PrusaLink: Port 80
SDCP: Port 80
OctoPrint: Port 80
```

### Timeout Settings
Default: 100ms per host (configurable in NetworkDiscoverySettings)

---

## Performance Impact

### Scan Time Comparison (254 IPs)

| Backends Selected | Ports Scanned | Estimated Time* |
|------------------|---------------|-----------------|
| All (4)          | 80, 7125      | ~60 seconds     |
| Moonraker only   | 7125          | ~30 seconds     |
| PrusaLink only   | 80            | ~30 seconds     |
| Moonraker + Prusa| 80, 7125      | ~60 seconds     |

*Times are estimates based on 100ms timeout per host

---

## Support

### Documentation Files
- `BACKEND_SELECTION_FEATURE.md` - Complete feature documentation
- `UI_MOCKUP.md` - Visual mockups of the UI
- `CODE_FLOW.md` - Detailed code flow and architecture
- `README.md` - Main project documentation

### Code Locations
- **Frontend UI**: `src/Web/ReactApp/src/components/PrinterDiscoveryModal.tsx`
- **Backend Service**: `src/api/Services/NetworkDiscoveryService.cs`
- **DTOs**: `src/shared/Models.cs`
- **Tests**: `src/tests/Farm.Web.Api.Tests/BackendSelectionTests.cs`

### Getting Help
1. Check this quick reference
2. Read the detailed documentation files
3. Check the code comments
4. Review the test cases for examples
