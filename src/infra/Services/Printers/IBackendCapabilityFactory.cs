using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;

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
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetFileDownloadClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file listings.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetFileListClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file uploads.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetFileUploadClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports starting prints.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetStartPrintClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports control operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetControlOperationsClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports camera operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetCameraClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file metadata extraction.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetFileMetadataClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports movement/positioning operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetMovementClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports temperature control.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetTemperatureControlClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports advanced printer information.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetPrinterInformationClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Gets all supported capabilities for a given backend.
    /// Useful for UI to determine which features to enable.
    /// </summary>
    /// <param name="backend">The printer backend type to query capabilities for.</param>
    BackendCapabilities GetSupportedCapabilities(PrinterBackend backend);

    /// <summary>
    /// Tries to get a backend client that supports history operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetHistoryClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports print job control operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetPrintJobControlClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client that supports file management operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the backend client if the capability is supported; otherwise, null.</param>
    bool TryGetFileManagementClient(PrinterBackend backend, out IBackendClient? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsCamera for camera operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed camera client if the capability is supported; otherwise, null.</param>
    bool TryGetCameraClientTyped(PrinterBackend backend, out ISupportsCamera? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsConfiguredCameraDetection for detecting configured cameras.
    /// This is used to query the printer's actual camera configuration and ONLY return URLs for cameras that exist.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed camera detection client if the capability is supported; otherwise, null.</param>
    bool TryGetConfiguredCameraDetectionClient(PrinterBackend backend, out ISupportsConfiguredCameraDetection? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsHistory for history operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed history client if the capability is supported; otherwise, null.</param>
    bool TryGetHistoryClientTyped(PrinterBackend backend, out ISupportsHistory? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsMovement for movement operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed movement client if the capability is supported; otherwise, null.</param>
    bool TryGetMovementClientTyped(PrinterBackend backend, out ISupportsMovement? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsTemperatureControl for temperature operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed temperature control client if the capability is supported; otherwise, null.</param>
    bool TryGetTemperatureControlClientTyped(PrinterBackend backend, out ISupportsTemperatureControl? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsControlOperations for print job control.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed control operations client if the capability is supported; otherwise, null.</param>
    bool TryGetControlOperationsClientTyped(PrinterBackend backend, out ISupportsControlOperations? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsFileUpload for file upload operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed file upload client if the capability is supported; otherwise, null.</param>
    bool TryGetFileUploadClientTyped(PrinterBackend backend, out ISupportsFileUpload? client);

    /// <summary>
    /// Tries to get a backend client typed as ISupportsStartPrint for starting print operations.
    /// </summary>
    /// <param name="backend">The printer backend type to get a client for.</param>
    /// <param name="client">When this method returns, contains the typed start print client if the capability is supported; otherwise, null.</param>
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
