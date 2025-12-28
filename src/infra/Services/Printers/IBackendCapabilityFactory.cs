using Farm.Infrastructure.Contracts.Printers;

namespace Farm.Infrastructure.Services.Printers;

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
    bool TryGetFileDownloadClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file listings.
    /// </summary>
    bool TryGetFileListClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file uploads.
    /// </summary>
    bool TryGetFileUploadClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports starting prints.
    /// </summary>
    bool TryGetStartPrintClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports control operations.
    /// </summary>
    bool TryGetControlOperationsClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports camera operations.
    /// </summary>
    bool TryGetCameraClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file metadata extraction.
    /// </summary>
    bool TryGetFileMetadataClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports movement/positioning operations.
    /// </summary>
    bool TryGetMovementClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports temperature control.
    /// </summary>
    bool TryGetTemperatureControlClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports advanced printer information.
    /// </summary>
    bool TryGetPrinterInformationClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Gets all supported capabilities for a given backend.
    /// Useful for UI to determine which features to enable.
    /// </summary>
    BackendCapabilities GetSupportedCapabilities(PrinterBackend backend);

    /// <summary>
    /// Tries to get a backend client that supports history operations.
    /// </summary>
    bool TryGetHistoryClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports print job control operations.
    /// </summary>
    bool TryGetPrintJobControlClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file management operations.
    /// </summary>
    bool TryGetFileManagementClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsCamera for camera operations.
    /// </summary>
    bool TryGetCameraClientTyped(PrinterBackend backend, out ISupportsCamera? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsConfiguredCameraDetection for detecting configured cameras.
    /// This is used to query the printer's actual camera configuration and ONLY return URLs for cameras that exist.
    /// </summary>
    bool TryGetConfiguredCameraDetectionClient(PrinterBackend backend, out ISupportsConfiguredCameraDetection? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsHistory for history operations.
    /// </summary>
    bool TryGetHistoryClientTyped(PrinterBackend backend, out ISupportsHistory? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsMovement for movement operations.
    /// </summary>
    bool TryGetMovementClientTyped(PrinterBackend backend, out ISupportsMovement? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsTemperatureControl for temperature operations.
    /// </summary>
    bool TryGetTemperatureControlClientTyped(PrinterBackend backend, out ISupportsTemperatureControl? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsControlOperations for print job control.
    /// </summary>
    bool TryGetControlOperationsClientTyped(PrinterBackend backend, out ISupportsControlOperations? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsFileUpload for file upload operations.
    /// </summary>
    bool TryGetFileUploadClientTyped(PrinterBackend backend, out ISupportsFileUpload? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsStartPrint for starting print operations.
    /// </summary>
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
