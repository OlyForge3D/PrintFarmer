# OctoPrint Client Quick Reference

## Native API Endpoints Used

### Axis Movement - /api/printer/printhead
```csharp
// Home all axes
POST /api/printer/printhead
{
  "command": "home",
  "axes": ["x", "y", "z"]
}

// Home XY only
POST /api/printer/printhead
{
  "command": "home",
  "axes": ["x", "y"]
}

// Jog (relative movement)
POST /api/printer/printhead
{
  "command": "jog",
  "x": 10,    // mm
  "y": -5,    // mm
  "z": 2      // mm
}
```

### Temperature - /api/printer/bed (Native) + /api/printer/command (Gcode)
```csharp
// Bed temperature (NATIVE API - PREFERRED)
POST /api/printer/bed
{
  "command": "target",
  "target": 60
}

// Hotend temperature (GCODE - no native endpoint)
POST /api/printer/command
{
  "command": "M104 S200"
}
```

### Connection - /api/connection
```csharp
// Connect
POST /api/connection
{
  "command": "connect"
}

// Disconnect
POST /api/connection
{
  "command": "disconnect"
}

// Get state
GET /api/connection
```

### Job Control - /api/job
```csharp
// Start print
POST /api/job
{
  "command": "select",
  "print": true,
  "file": "filename.gcode"
}

// Pause
POST /api/job
{
  "command": "pause",
  "action": "pause"
}

// Resume
POST /api/job
{
  "command": "pause",
  "action": "resume"
}

// Cancel
POST /api/job
{
  "command": "cancel"
}
```

### Generic Gcode - /api/printer/command
```csharp
// Send any gcode
POST /api/printer/command
{
  "command": "G28"
}

// Multi-line gcode
POST /api/printer/command
{
  "command": "G28\nM104 S200\nM140 S60"
}
```

## Method Reference

### Axis Movement (Native API)
```csharp
Task<bool> SendHomeAsync(string baseUrl, string apiKey)
Task<bool> HomeXYAsync(string baseUrl, string apiKey)
Task<bool> HomeZAsync(string baseUrl, string apiKey)
Task<bool> JogAsync(string baseUrl, string apiKey, double? x, double? y, double? z, double? speed)
```

### Temperature
```csharp
Task<bool> SetBedTempAsync(string baseUrl, string apiKey, double bedTemp)
Task<bool> SetHotendTempAsync(string baseUrl, string apiKey, double hotendTemp)
Task<bool> SetTempsAsync(string baseUrl, string apiKey, double? hotend, double? bed)
```

### Connection Management
```csharp
Task<bool> ConnectAsync(string baseUrl, string apiKey)
Task<bool> DisconnectAsync(string baseUrl, string apiKey)
Task<string> GetConnectionStateAsync(string baseUrl, string apiKey)
```

### Job Control
```csharp
Task<bool> StartJobAsync(string baseUrl, string apiKey, string fileName)
Task<bool> PauseAsync(string baseUrl, string apiKey)
Task<bool> ResumeAsync(string baseUrl, string apiKey)
Task<bool> CancelJobAsync(string baseUrl, string apiKey)
Task<bool> CancelPrintAsync(string baseUrl, string apiKey)
```

### File Operations
### File Operations
```csharp
// Query existing files
Task<string[]> GetFileListAsync(string baseUrl, string apiKey)
Task<string> GetHistoryListAsync(string baseUrl, string apiKey, int? limit, int? start)
Task<string> GetHistoryJobAsync(string baseUrl, string apiKey, string jobId)

// Get file details
Task<string> GetFileDetailsAsync(string baseUrl, string apiKey, string path)

// Move/rename files
Task<bool> MoveFileAsync(string baseUrl, string apiKey, string source, string destination)

// Delete files/folders
Task<bool> DeleteFileAsync(string baseUrl, string apiKey, string path)

// Create folders
Task<bool> CreateFolderAsync(string baseUrl, string apiKey, string path, string folderName)

// Upload files
Task<bool> UploadFileAsync(string baseUrl, string apiKey, byte[] fileContent, string fileName, string? path, bool startPrint)
```

````

### Diagnostics
```csharp
Task<bool> TestConnectionAsync(string baseUrl, string apiKey)
Task<string> GetPrinterStateAsync(string baseUrl, string apiKey)
Task<string> GetJobStatusAsync(string baseUrl, string apiKey)
Task<string> GetCameraStreamUrlAsync(string baseUrl, string apiKey)
```

### Generic
```csharp
Task<bool> SendGcodeAsync(string baseUrl, string apiKey, string gcode)
Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
```

## Key Implementation Details

### JSON Serialization
- ✅ Use `JsonSerializer.Serialize()` for all API payloads
- ✅ Prevents escaping bugs with special characters
- ✅ Handles newlines correctly in multi-command gcode

### Temperature Hybrid Approach
- Bed: Uses native `/api/printer/bed` endpoint (preferred)
- Hotend: Uses M104 gcode (no native endpoint available)
- Both: SetTempsAsync() calls both appropriately and aggregates success status

### Home Commands
- All use native `/api/printer/printhead` API
- More OctoPrint-idiomatic than gcode approach
- Better error handling and integration

### Error Handling
- All methods return `bool` for success/failure
- Exception handling built in for network/parsing errors
- HTTP status codes determine success

## Usage Example

```csharp
// Inject IOctoPrintClient
public class MyService
{
    private readonly IOctoPrintClient _octoprint;
    
    public MyService(IOctoPrintClient octoprint)
    {
        _octoprint = octoprint;
    }
    
    public async Task HomeAndHeatAsync(string baseUrl, string apiKey)
    {
        // Home all axes
        bool homeSuccess = await _octoprint.SendHomeAsync(baseUrl, apiKey);
        if (!homeSuccess) throw new Exception("Home failed");
        
        // Set temperatures
        bool tempSuccess = await _octoprint.SetTempsAsync(baseUrl, apiKey, 200, 60);
        if (!tempSuccess) throw new Exception("Temperature setting failed");
    }
}
```

## Testing

When testing against real OctoPrint:
1. ✅ Verify API key is valid and has permission
2. ✅ Ensure printer is connected to OctoPrint
3. ✅ Check that axes are homed before relative movements
4. ✅ Monitor OctoPrint web interface during tests
5. ✅ Use browser DevTools Network tab to inspect requests/responses

## Related Documentation

- Full Audit: `OCTOPRINT_CLIENT_AUDIT.md`
- Session Report: `OCTOPRINT_CLIENT_SESSION_REPORT.md`
- Implementation: `src/api/Services/OctoPrintClient.cs`
- Interface: `src/api/Services/Interfaces/IOctoPrintClient.cs`
