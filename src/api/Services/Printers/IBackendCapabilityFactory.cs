using Farm.Infrastructure;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Printers;

/// <summary>
/// Factory for retrieving backend clients filtered by specific capabilities.
/// This provides a cleaner API than checking "is ISupportsFileList" everywhere in services.
/// 
/// Example usage:
///   // Get a client that can download files
///   if (capabilityFactory.TryGetFileDownloadClient(backend, out var downloadClient))
///   {
///       bytes = await downloadClient.DownloadFileAsync(url, filePath);
///   }
/// </summary>
public interface IBackendCapabilityFactory
{
    /// <summary>
    /// Tries to get a backend client that supports file downloads.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting downloads, or null if not supported</param>
    /// <returns>True if the backend supports file downloads, false otherwise</returns>
    bool TryGetFileDownloadClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file listings.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting file listing, or null if not supported</param>
    /// <returns>True if the backend supports file listing, false otherwise</returns>
    bool TryGetFileListClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file uploads.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting uploads, or null if not supported</param>
    /// <returns>True if the backend supports file uploads, false otherwise</returns>
    bool TryGetFileUploadClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports starting prints.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting print start, or null if not supported</param>
    /// <returns>True if the backend supports starting prints, false otherwise</returns>
    bool TryGetStartPrintClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports control operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting control operations, or null if not supported</param>
    /// <returns>True if the backend supports control operations, false otherwise</returns>
    bool TryGetControlOperationsClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports camera operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting camera operations, or null if not supported</param>
    /// <returns>True if the backend supports camera operations, false otherwise</returns>
    bool TryGetCameraClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file metadata extraction.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting file metadata, or null if not supported</param>
    /// <returns>True if the backend supports file metadata, false otherwise</returns>
    bool TryGetFileMetadataClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports movement/positioning operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting movement, or null if not supported</param>
    /// <returns>True if the backend supports movement operations, false otherwise</returns>
    bool TryGetMovementClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports temperature control.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting temperature control, or null if not supported</param>
    /// <returns>True if the backend supports temperature control, false otherwise</returns>
    bool TryGetTemperatureControlClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports advanced printer information.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting printer information, or null if not supported</param>
    /// <returns>True if the backend supports detailed printer information, false otherwise</returns>
    bool TryGetPrinterInformationClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Gets all supported capabilities for a given backend.
    /// Useful for UI to determine which features to enable.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <returns>A set of supported capability types for this backend</returns>
    BackendCapabilities GetSupportedCapabilities(PrinterBackend backend);

    /// <summary>
    /// Tries to get a backend client that supports history operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting history, or null if not supported</param>
    /// <returns>True if the backend supports history, false otherwise</returns>
    bool TryGetHistoryClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports print job control operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting print job control, or null if not supported</param>
    /// <returns>True if the backend supports print job control, false otherwise</returns>
    bool TryGetPrintJobControlClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file management operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting file management, or null if not supported</param>
    /// <returns>True if the backend supports file management, false otherwise</returns>
    bool TryGetFileManagementClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsCamera for camera operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting camera operations, or null if not supported</param>
    /// <returns>True if the backend supports camera operations, false otherwise</returns>
    bool TryGetCameraClientTyped(PrinterBackend backend, out ISupportsCamera? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsHistory for history operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting history, or null if not supported</param>
    /// <returns>True if the backend supports history, false otherwise</returns>
    bool TryGetHistoryClientTyped(PrinterBackend backend, out ISupportsHistory? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsMovement for movement operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting movement, or null if not supported</param>
    /// <returns>True if the backend supports movement operations, false otherwise</returns>
    bool TryGetMovementClientTyped(PrinterBackend backend, out ISupportsMovement? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsTemperatureControl for temperature operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting temperature control, or null if not supported</param>
    /// <returns>True if the backend supports temperature control, false otherwise</returns>
    bool TryGetTemperatureControlClientTyped(PrinterBackend backend, out ISupportsTemperatureControl? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsControlOperations for print job control.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting control operations, or null if not supported</param>
    /// <returns>True if the backend supports control operations, false otherwise</returns>
    bool TryGetControlOperationsClientTyped(PrinterBackend backend, out ISupportsControlOperations? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsFileUpload for file upload operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting file uploads, or null if not supported</param>
    /// <returns>True if the backend supports file uploads, false otherwise</returns>
    bool TryGetFileUploadClientTyped(PrinterBackend backend, out ISupportsFileUpload? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsStartPrint for starting print operations.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <param name="client">The client supporting start print, or null if not supported</param>
    /// <returns>True if the backend supports starting prints, false otherwise</returns>
    bool TryGetStartPrintClientTyped(PrinterBackend backend, out ISupportsStartPrint? client);
}

/// <summary>
/// Flags indicating which capabilities a backend supports.
/// Can be combined: var caps = BackendCapabilities.Download | BackendCapabilities.Upload;
/// </summary>
[Flags]
public enum BackendCapabilities
{
    None = 0,
    FileDownload = 1 << 0,
    FileList = 1 << 1,
    FileUpload = 1 << 2,
    StartPrint = 1 << 3,
    ControlOperations = 1 << 4,
    Camera = 1 << 5,
    FileMetadata = 1 << 6,
    Movement = 1 << 7,
    TemperatureControl = 1 << 8,
    PrinterInformation = 1 << 9,

    /// <summary>All file operations (download, list, upload)</summary>
    FileOperations = FileDownload | FileList | FileUpload,

    /// <summary>All information retrieval operations (metadata, printer info, etc.)</summary>
    InformationRetrieval = FileMetadata | PrinterInformation,

    /// <summary>All control operations (pause, resume, stop, home, move, temperature)</summary>
    AllControlOps = ControlOperations | Movement | TemperatureControl,

    /// <summary>All capabilities combined</summary>
    All = FileDownload | FileList | FileUpload | StartPrint | ControlOperations | Camera | FileMetadata | Movement | TemperatureControl | PrinterInformation
}
