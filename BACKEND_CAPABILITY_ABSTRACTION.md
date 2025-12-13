# Backend Client Capability Abstraction Pattern

## Overview

This document describes the new capability-based abstraction pattern for backend client services in PrintFarmer. Instead of consuming services having explicit conditional logic based on printer backend types (Moonraker, PrusaLink, SDCP, OctoPrint), clients now use marker interfaces to advertise their capabilities.

## Problem Statement

Previously, services like `GcodeHarvestService` had to perform explicit backend type checks and call different internal helper methods:

```csharp
// OLD PATTERN - Backend branching scattered throughout code
List<PrinterFileInfo> fileList = backend switch
{
    PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(...),
    PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(...),
    PrinterBackend.SDCP => await GetSdcpFilesAsync(...),
    _ => new List<PrinterFileInfo>()
};
```

This approach:
- Creates tight coupling between services and specific backend implementations
- Requires updating service code whenever a new backend is added
- Distributes backend-specific logic across multiple methods
- Makes it harder to understand what capabilities a backend actually supports

## Solution: Capability Marker Interfaces

We've introduced capability marker interfaces that advertise what a backend client can do:

```csharp
public interface ISupportsFileDownload { }
public interface ISupportsFileList { }
public interface ISupportsFileUpload { }
public interface ISupportsStartPrint { }
public interface ISupportsControlOperations { }
```

These are pure marker interfaces - they define no methods. Their purpose is to serve as capability indicators.

## Implementation

### 1. Backend Client Interface Updates

Each backend client interface now implements the capability markers it supports:

```csharp
// Moonraker supports all file operations
public interface IMoonrakerClient : IBackendClient, 
    ISupportsFileDownload, ISupportsFileList, ISupportsFileUpload, ISupportsStartPrint
{ }

// PrusaLink supports most operations but not download
public interface IPrusaLinkClient : IBackendClient, 
    ISupportsFileList, ISupportsFileUpload, ISupportsStartPrint
{ }

// SDCP supports file listing
public interface ISdcpClient : IBackendClient, ISupportsFileList
{ }

// OctoPrint supports most operations
public interface IOctoPrintClient : IBackendClient, 
    ISupportsFileDownload, ISupportsFileList, ISupportsFileUpload
{ }
```

### 2. Service-Side Usage with Capability Checking

Services use the `IBackendClientFactory` to get a client, then use `is` pattern matching to check capabilities:

```csharp
// NEW PATTERN - Capability-based checking
IBackendClient client = _backendClientFactory.GetClient(backend);

if (client is ISupportsFileList)
{
    // Only proceed if the client actually supports file listing
    fileList = backend switch
    {
        PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(...),
        PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(...),
        PrinterBackend.SDCP => await GetSdcpFilesAsync(...),
        _ => new List<PrinterFileInfo>()
    };
}
else
{
    logger.LogWarning($"Backend {backend} does not support file listing");
}
```

Or, for clients that DO have the method directly implemented:

```csharp
if (client is ISupportsFileDownload)
{
    // Moonraker is the only one with direct download support
    return backend == PrinterBackend.Moonraker
        ? await ConvertBytesToMemoryStreamAsync(
            await _moonraker.DownloadFileAsync(printer.ServerUrl, filePath))
        : null;
}
```

## Benefits

1. **Extensibility**: Adding a new backend only requires:
   - Creating a new backend client interface
   - Implementing appropriate capability markers
   - No changes needed to consuming services (unless they need backend-specific logic)

2. **Clarity**: The capabilities are explicit and self-documenting:
   - Looking at `IPrusaLinkClient : ... ISupportsFileUpload` immediately shows it can upload files
   - Missing `ISupportsFileDownload` clearly indicates file downloads are not available

3. **Type Safety**: Capability checking is done at compile-time with pattern matching
   - The compiler understands the capability contracts
   - Refactoring becomes safer

4. **Loose Coupling**: Services don't need to know the specific implementation details
   - They only care about what a backend CAN do, not HOW it does it
   - Internal client method names and signatures can vary without affecting services

5. **Graceful Fallbacks**: Services can handle unsupported capabilities elegantly:
   ```csharp
   if (client is ISupportsFileDownload)
   {
       // Try to download
   }
   else
   {
       // Use alternative approach or return null
       logger.LogWarning("Download not supported for this backend");
   }
   ```

## Migration Path for Existing Code

When refactoring existing services to use this pattern:

1. Identify all `switch` statements that branch on `PrinterBackend`
2. For each case, determine what capability is being exercised
3. Update the backend client interfaces to implement the relevant capability marker
4. Replace the switch statement with capability checking using `is` pattern matching
5. Test thoroughly to ensure all backends work as expected

## Future Enhancements

This pattern enables several future improvements:

1. **Runtime Capability Discovery**: Query what backends support dynamically
   ```csharp
   var uploadCapableBackends = _backendClientFactory
       .GetAllClients()
       .OfType<ISupportsFileUpload>()
       .ToList();
   ```

2. **Capability-Based UI**: Show UI features only for backends that support them
   ```csharp
   if (printer.Backend is ISupportsFileDownload)
   {
       // Show download button
   }
   ```

3. **Generic Operations**: Implement operations that work with any backend supporting a capability
   ```csharp
   public async Task<bool> GenericUploadAsync<T>(T client, string path, Stream content) 
       where T : IBackendClient, ISupportsFileUpload
   {
       // Implementation independent of specific backend type
   }
   ```

## Related Files

- Capability interface definitions: [src/api/Services/Interfaces/IBackendClientCapabilities.cs](src/api/Services/Interfaces/IBackendClientCapabilities.cs)
- Backend client interfaces:
  - [src/api/Services/Interfaces/IMoonrakerClient.cs](src/api/Services/Interfaces/IMoonrakerClient.cs)
  - [src/api/Services/Interfaces/IPrusaLinkClient.cs](src/api/Services/Interfaces/IPrusaLinkClient.cs)
  - [src/api/Services/Interfaces/ISdcpClient.cs](src/api/Services/Interfaces/ISdcpClient.cs)
  - [src/api/Services/Interfaces/IOctoPrintClient.cs](src/api/Services/Interfaces/IOctoPrintClient.cs)
- Example refactoring: [src/api/Services/GcodeHarvestService.cs](src/api/Services/GcodeHarvestService.cs) (see `DiscoverAndQueueFilesAsync` and `DownloadFileAsync` methods)

## Example: GcodeHarvestService

The `GcodeHarvestService` was the first service refactored to use this pattern. Before:

```csharp
List<PrinterFileInfo> fileList = backend switch
{
    PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(...),
    PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(...),
    PrinterBackend.SDCP => await GetSdcpFilesAsync(...),
    _ => new List<PrinterFileInfo>()
};
```

After:

```csharp
IBackendClient client = _backendClientFactory.GetClient(backend);

if (client is ISupportsFileList)
{
    fileList = backend switch
    {
        PrinterBackend.Moonraker => await GetMoonrakerFilesAsync(...),
        PrinterBackend.PrusaLink => await GetPrusaLinkFilesAsync(...),
        PrinterBackend.SDCP => await GetSdcpFilesAsync(...),
        _ => new List<PrinterFileInfo>()
    };
}
```

The key difference: the `is ISupportsFileList` check ensures we don't attempt unsupported operations, making the service more robust and maintainable.
