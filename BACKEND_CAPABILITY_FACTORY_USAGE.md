# Using the Backend Capability Factory

## Overview

The `IBackendCapabilityFactory` provides a clean, fluent API for checking and accessing backend capabilities without scattered `is ISupportsXxx` checks throughout your code.

## Before: Capability Checking Without Factory

```csharp
// OLD PATTERN - Verbose and repetitive
public async Task DiscoverFiles(PrinterBackend backend, string url, string? apiKey)
{
    IBackendClient client = _backendClientFactory.GetClient(backend);
    
    if (client is ISupportsFileList)
    {
        // Now what? You still need to call backend-specific methods
        // because different backends have different signatures
        List<PrinterFileInfo> fileList = backend switch
        {
            PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(url),
            PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(url, apiKey),
            PrinterBackend.SDCP => await GetSdcpFilesAsync(url),
            _ => new List<PrinterFileInfo>()
        };
    }
    else
    {
        logger.LogWarning($"Backend {backend} does not support file listing");
    }
}
```

## After: Clean Capability Factory Usage

```csharp
public async Task DiscoverFiles(PrinterBackend backend, string url, string? apiKey)
{
    // NEW PATTERN - Clean and expressive
    if (_capabilityFactory.TryGetFileListClient(backend, out var client))
    {
        // Client is guaranteed to support file listing
        // Backend-specific logic still lives in backend-specific helpers
        List<PrinterFileInfo> fileList = backend switch
        {
            PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(url),
            PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(url, apiKey),
            PrinterBackend.SDCP => await GetSdcpFilesAsync(url),
            _ => new List<PrinterFileInfo>()
        };
    }
    else
    {
        logger.LogWarning($"Backend {backend} does not support file listing");
    }
}
```

The benefit: `TryGetFileListClient` makes the intention clear and documents what capability is being checked.

## Usage Examples

### 1. Check a Single Capability

```csharp
// Check if backend supports file downloads
if (_capabilityFactory.TryGetFileDownloadClient(backend, out var client))
{
    byte[]? bytes = await moonrakerClient.DownloadFileAsync(url, filePath);
    // Process bytes...
}
else
{
    logger.LogWarning($"Backend {backend} does not support file downloads");
}
```

### 2. Check Multiple Capabilities

```csharp
public async Task<bool> CanUploadAndStartPrint(PrinterBackend backend)
{
    bool canUpload = _capabilityFactory.TryGetFileUploadClient(backend, out _);
    bool canStartPrint = _capabilityFactory.TryGetStartPrintClient(backend, out _);
    
    return canUpload && canStartPrint;
}
```

### 3. Query All Supported Capabilities

```csharp
public void ConfigureUiForBackend(PrinterBackend backend)
{
    var capabilities = _capabilityFactory.GetSupportedCapabilities(backend);
    
    // Show UI features only for supported capabilities
    enableDownloadButton = capabilities.HasFlag(BackendCapabilities.FileDownload);
    enableUploadButton = capabilities.HasFlag(BackendCapabilities.FileUpload);
    enableStartPrintButton = capabilities.HasFlag(BackendCapabilities.StartPrint);
    
    // Or use compound flags
    canDoAllFileOps = (capabilities & BackendCapabilities.FileOperations) == BackendCapabilities.FileOperations;
}
```

### 4. Filter Backends by Capability

```csharp
public List<PrinterBackend> GetBackendsSupportingDownload()
{
    return new[]
    {
        PrinterBackend.Moonraker,
        PrinterBackend.PrusaLink,
        PrinterBackend.SDCP,
        PrinterBackend.OctoPrint
    }
    .Where(backend => 
        _capabilityFactory.TryGetFileDownloadClient(backend, out _))
    .ToList();
    
    // Result: [Moonraker, OctoPrint]
}
```

## Capability Flags Reference

```csharp
[Flags]
public enum BackendCapabilities
{
    None = 0,
    FileDownload = 1 << 0,      // Can download files
    FileList = 1 << 1,           // Can list files
    FileUpload = 1 << 2,         // Can upload files
    StartPrint = 1 << 3,         // Can start print jobs
    ControlOperations = 1 << 4,  // Can pause, resume, stop, etc.
    
    // Convenient composite flags
    FileOperations = FileDownload | FileList | FileUpload,
    All = FileDownload | FileList | FileUpload | StartPrint | ControlOperations
}
```

### Current Backend Capabilities

| Backend | Download | List | Upload | Print | Control |
|---------|----------|------|--------|-------|---------|
| Moonraker | ✅ | ✅ | ✅ | ✅ | ✅ |
| PrusaLink | ❌ | ✅ | ✅ | ✅ | ❌ |
| SDCP | ❌ | ✅ | ❌ | ❌ | ❌ |
| OctoPrint | ✅ | ✅ | ✅ | ❌ | ❌ |

## Dependency Injection

```csharp
public class GcodeHarvestService
{
    private readonly IBackendCapabilityFactory _capabilityFactory;
    
    public GcodeHarvestService(IBackendCapabilityFactory capabilityFactory)
    {
        _capabilityFactory = capabilityFactory;
    }
    
    // Use it in your methods...
}
```

The factory is registered as a singleton in `ServiceCollectionExtensions.cs`:

```csharp
services.AddSingleton<IBackendCapabilityFactory, BackendCapabilityFactory>();
```

## Benefits Over Direct Capability Checking

1. **Self-Documenting**: Method names clearly express intent
   - `TryGetFileListClient` is clearer than `is ISupportsFileList`

2. **Centralized Configuration**: Capabilities are defined in one place
   - Easy to update capability mappings for a backend
   - Single source of truth

3. **Abstraction**: Services don't need to know about marker interfaces
   - Just use the factory methods
   - Less coupling to implementation details

4. **Query Capabilities**: Discover what a backend can do without getting a client
   - `GetSupportedCapabilities()` useful for UI configuration
   - Useful for logging and diagnostics

5. **Caching**: Capability checks are pre-computed at startup
   - Dictionary lookup O(1), no reflection needed
   - More efficient than runtime `is` checks

## Migration Path

When refactoring existing services to use this factory:

1. Inject `IBackendCapabilityFactory` into the service
2. Replace direct `is ISupportsXxx` checks with `TryGetXxxClient` calls
3. Keep backend-specific logic in internal helper methods
4. Add support for new capabilities by updating the capabilities dictionary

Example:

```csharp
// BEFORE
if (client is ISupportsFileList)
{
    // Call GetMoonrakerFilesAsync, GetPrusaLinkFilesAsync, etc.
}

// AFTER
if (_capabilityFactory.TryGetFileListClient(backend, out var client))
{
    // Call GetMoonrakerFilesAsync, GetPrusaLinkFilesAsync, etc.
}
```

The net effect: same functionality, but with a cleaner, more discoverable API.
