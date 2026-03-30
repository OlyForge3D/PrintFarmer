using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Capability marker interface for backend clients that support file download functionality.
/// Backends implementing this interface can retrieve the complete contents of files stored on the printer.
/// </summary>
public interface ISupportsFileDownload
{
    /// <summary>
    /// Downloads the complete contents of a file from the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server (e.g., http://printer-ip)</param>
    /// <param name="filePath">The path to the file to download (e.g., "gcodes/model.gcode")</param>
    /// <param name="ct">Cancellation token to cancel the download operation</param>
    /// <returns>The file contents as a byte array, or null if the file does not exist or download fails</returns>
    Task<byte[]?> DownloadFileAsync(string baseUrl, string filePath, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support file list retrieval.
/// Backends implementing this interface can enumerate and retrieve information about files stored on the printer.
/// </summary>
public interface ISupportsFileList
{
    /// <summary>
    /// Retrieves a list of files stored on the printer with basic information (name, size, modification date).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server (e.g., http://printer-ip)</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A list of PrinterFileInfo objects containing file metadata, or empty list if no files found</returns>
    Task<List<PrinterFileInfo>> GetFileListAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support file upload functionality.
/// Backends implementing this interface can receive and store G-code files on the printer.
/// </summary>
public interface ISupportsFileUpload
{
    /// <summary>
    /// Uploads a G-code file to the printer's storage.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server (e.g., http://printer-ip)</param>
    /// <param name="fileName">The name for the uploaded file (e.g., "model.gcode")</param>
    /// <param name="fileContent">The file content stream to upload</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the upload operation</param>
    /// <returns>True if upload succeeded, false if it failed</returns>
    Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support starting print jobs.
/// Backends implementing this interface can initiate printing of a G-code file stored on the printer.
/// </summary>
public interface ISupportsStartPrint
{
    /// <summary>
    /// Starts a print job using a G-code file stored on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server (e.g., http://printer-ip)</param>
    /// <param name="fileName">The path/name of the G-code file to print (e.g., "gcodes/model.gcode")</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if print started successfully, false if the file was not found or printer was not ready</returns>
    Task<bool> StartPrintAsync(string baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Stages of the combined upload-and-start-print workflow, reported via IProgress.
/// </summary>
public enum UploadAndPrintStage
{
    /// <summary>File upload in progress.</summary>
    Uploading,

    /// <summary>Backend-specific processing after upload (e.g., firmware file indexing).</summary>
    Processing,

    /// <summary>Sending start-print command to the printer.</summary>
    StartingPrint,

    /// <summary>Both upload and start-print completed successfully.</summary>
    Completed,

    /// <summary>The operation failed at some step.</summary>
    Failed
}

/// <summary>
/// Result of a combined upload-and-start-print operation.
/// </summary>
/// <param name="Success">Whether both upload and start-print succeeded.</param>
/// <param name="FailedStage">The stage at which the operation failed, or <see cref="UploadAndPrintStage.Completed"/> on success.</param>
/// <param name="ErrorMessage">Human-readable error message when <paramref name="Success"/> is false.</param>
public sealed record UploadAndPrintResult(
    bool Success,
    UploadAndPrintStage FailedStage = UploadAndPrintStage.Completed,
    string? ErrorMessage = null)
{
    /// <summary>Creates a successful result.</summary>
    public static UploadAndPrintResult Ok() => new(true);

    /// <summary>Creates a failure result indicating which stage failed.</summary>
    public static UploadAndPrintResult Fail(UploadAndPrintStage stage, string? message = null)
        => new(false, stage, message);
}

/// <summary>
/// Capability marker interface for backend clients that support combined upload-and-start-print workflow.
/// Backends implementing this interface handle the full sequence of uploading a G-code file
/// and starting it in a single operation, allowing backend-specific delays, retries, or path
/// resolution between upload and start.
/// </summary>
public interface ISupportsUploadAndPrint
{
    /// <summary>
    /// Uploads a G-code file to the printer and starts printing it.
    /// Each backend implementation handles any required delays, path resolution,
    /// or protocol-specific steps between upload and print start.
    /// Progress is reported via <paramref name="progress"/> so callers can relay stage updates to the UI.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server (e.g., http://printer-ip)</param>
    /// <param name="fileName">The name for the uploaded file (e.g., "model.gcode")</param>
    /// <param name="fileContent">The file content stream to upload</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="progress">Optional progress reporter for stage transitions (Uploading → Processing → StartingPrint → Completed/Failed)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>An <see cref="UploadAndPrintResult"/> indicating success or which stage failed</returns>
    Task<UploadAndPrintResult> UploadAndStartPrintAsync(
        string baseUrl,
        string fileName,
        Stream fileContent,
        PrinterCredential? credential = null,
        IProgress<UploadAndPrintStage>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support printer control operations.
/// Provides pause, resume, and cancel operations for managing active print jobs.
/// </summary>
public interface ISupportsControlOperations
{
    /// <summary>
    /// Pauses the currently executing print job.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if pause command succeeded, false if no job is active or pause failed</returns>
    Task<bool> PauseAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Resumes a paused print job.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if resume command succeeded, false if no paused job exists or resume failed</returns>
    Task<bool> ResumeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Cancels the currently executing print job.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if cancel command succeeded, false if no job is active or cancel failed</returns>
    Task<bool> CancelAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support camera operations.
/// Provides methods to retrieve camera stream and snapshot URLs for displaying live printer footage.
/// </summary>
public interface ISupportsCamera
{
    /// <summary>
    /// Gets the URL for a live camera stream from the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="frontendPort">Optional frontend port number if different from the backend port</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>The camera stream URL, or null if no camera is available or stream cannot be retrieved</returns>
    Task<string?> GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the URL for a camera snapshot (still image) from the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="frontendPort">Optional frontend port number if different from the backend port</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>The camera snapshot URL, or null if no camera is available or snapshot cannot be retrieved</returns>
    Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support detecting configured cameras.
/// This interface detects ONLY cameras that are actually configured on the printer, preventing false positives.
/// Returns null for both stream and snapshot if no cameras are found.
/// Implementations MUST validate camera existence before returning URLs to avoid saving camera URLs for printers without cameras.
/// </summary>
public interface ISupportsConfiguredCameraDetection
{
    /// <summary>
    /// Detects and returns camera URLs for cameras actually configured on the printer.
    /// This method performs validation to ensure cameras exist before returning URLs.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="frontendPort">Optional frontend port number if different from the backend port</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>
    /// A tuple containing (streamUrl, snapshotUrl). Both values are null if no cameras are configured.
    /// Only non-null values represent actually configured and accessible cameras.
    /// </returns>
    Task<(string? StreamUrl, string? SnapshotUrl)> DetectConfiguredCameraUrlsAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support file metadata extraction.
/// Extracts detailed information from G-code files including print time estimates, layer information, thumbnails, and slicer settings.
/// </summary>
public interface ISupportsFileMetadata
{
    /// <summary>
    /// Extracts metadata from a G-code file stored on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="filePath">The path to the G-code file (e.g., "gcodes/model.gcode")</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>PrinterFileMetadata containing print time, layer height, temperatures, and thumbnail information, or null if metadata cannot be extracted</returns>
    Task<PrinterFileMetadata?> GetFileMetadataAsync(string baseUrl, string filePath, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support printer movement and positioning operations.
/// Backends implement whatever movement methods they support; the API uses capability checking to call appropriate methods.
/// All distances are in millimeters, feed rate (f) is in mm/min.
/// </summary>
public interface ISupportsMovement
{
    /// <summary>
    /// Sends the printer to home position for all axes (X, Y, Z).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if home command succeeded, false if operation failed</returns>
    Task<bool> HomeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Sends the printer to home position for all axes (alternative implementation).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if home command succeeded, false if operation failed</returns>
    Task<bool> SendHomeAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Homes the X and Y axes only, leaving Z position unchanged.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if home XY command succeeded, false if operation failed</returns>
    Task<bool> HomeXYAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Homes the Z axis only, leaving X and Y positions unchanged.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if home Z command succeeded, false if operation failed</returns>
    Task<bool> HomeZAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the printer by the specified distances (relative movement).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="x">Relative X distance in millimeters, or null to not move X axis</param>
    /// <param name="y">Relative Y distance in millimeters, or null to not move Y axis</param>
    /// <param name="z">Relative Z distance in millimeters, or null to not move Z axis</param>
    /// <param name="f">Feed rate in mm/min, or null to use default speed</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if move command succeeded, false if operation failed</returns>
    Task<bool> MoveAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the printer to an absolute position (absolute positioning).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="x">Absolute X position in millimeters, or null to not move X axis</param>
    /// <param name="y">Absolute Y position in millimeters, or null to not move Y axis</param>
    /// <param name="z">Absolute Z position in millimeters, or null to not move Z axis</param>
    /// <param name="f">Feed rate in mm/min, or null to use default speed</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if move command succeeded, false if operation failed</returns>
    Task<bool> MoveToAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support temperature control.
/// Provides basic temperature control supported across multiple backends for setting hotend and bed temperatures.
/// </summary>
public interface ISupportsTemperatureControl
{
    /// <summary>
    /// Sets target temperatures for the hotend and/or bed heaters.
    /// Pass null for a heater to leave it unchanged (e.g., hotendTemp=210, bedTemp=null sets only hotend).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="hotendTemp">Target hotend temperature in Celsius, or null to leave unchanged</param>
    /// <param name="bedTemp">Target bed temperature in Celsius, or null to leave unchanged</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if temperature commands succeeded, false if operation failed</returns>
    Task<bool> SetTemperaturesAsync(string baseUrl, double? hotendTemp = null, double? bedTemp = null, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support advanced printer information retrieval.
/// Provides access to printer name, firmware version, and model information.
/// </summary>
public interface ISupportsPrinterInformation
{
    /// <summary>
    /// Retrieves detailed printer information including name, firmware version, and model.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>StandardPrinterInfo containing printer name, firmware, and model information</returns>
    Task<StandardPrinterInfo> GetPrinterInformationAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support job history retrieval.
/// Provides access to completed and failed print jobs, history statistics, and history management.
/// </summary>
public interface ISupportsHistory
{
    /// <summary>
    /// Retrieves a paginated list of completed or failed print jobs from history.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="limit">Maximum number of history entries to return, or null for default limit</param>
    /// <param name="start">Starting index for pagination, or null to start from beginning</param>
    /// <param name="since">Filter to only return jobs started after this UTC timestamp (for incremental seeding)</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>HistoryListResponse containing paginated history entries, or null if history cannot be retrieved</returns>
    /// <remarks>
    /// The 'since' parameter enables incremental history seeding. Backends that support server-side
    /// filtering (Moonraker) will use it directly. Backends that don't (OctoPrint) will filter client-side.
    /// </remarks>
    Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, DateTime? since = null, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves detailed information about a specific historical print job.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="jobId">The unique identifier of the history job to retrieve</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>HistoryJob containing detailed job information, or null if job not found</returns>
    Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves aggregated statistics about all historical print jobs (total prints, total time, etc.).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>HistoryTotals containing aggregated statistics, or null if statistics cannot be retrieved</returns>
    Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes a specific print job from the history.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="jobId">The unique identifier of the history job to delete</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if deletion succeeded, false if job not found or deletion failed</returns>
    Task<bool> DeleteHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support basic printer status retrieval.
/// Provides standardized online/offline status and printer state information.
/// </summary>
public interface ISupportsStatus
{
    /// <summary>
    /// Retrieves the current status of the printer (online/offline state).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>PrinterStatus containing online status and current printer state</returns>
    Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support composite status retrieval.
/// Provides detailed status including printer position, temperatures, active job info, camera URLs, and build state.
/// Typically supported by Moonraker and SDCP backends.
/// </summary>
public interface ISupportsCompositeStatus
{
    /// <summary>
    /// Retrieves comprehensive printer status including position, temperatures, job progress, and media streams.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>PrinterCompositeStatus containing detailed printer state, position, temperatures, job info, and camera URLs</returns>
    Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support current job status retrieval.
/// Provides detailed information about the currently active or last completed print job.
/// </summary>
public interface ISupportsJobControl
{
    /// <summary>
    /// Retrieves information about the current or last print job.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>PrinterJob containing current/last job information, or null if no job exists</returns>
    Task<PrinterJob?> GetJobAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support Moonraker spoolman integration.
/// Provides access to spool information and tracking for material management.
/// Spoolman is a companion service for Moonraker that tracks filament spools and usage.
/// </summary>
public interface ISupportsSpoolman
{
    /// <summary>
    /// Retrieves the ID of the currently active/loaded spool in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>The ID of the active spool, or null if no spool is active or Spoolman is unavailable</returns>
    Task<int?> GetSpoolmanActiveSpoolAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Retrieves detailed information about a specific spool by ID.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="spoolId">The unique ID of the spool to retrieve</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>JSON string containing spool information, or null if spool not found</returns>
    Task<string?> GetSpoolmanSpoolByIdAsync(string baseUrl, int spoolId, CancellationToken ct = default);

    /// <summary>
    /// Sets or clears the active spool in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="spoolId">The spool ID to activate, or null to clear the active spool</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if the operation succeeded</returns>
    Task<bool> SetSpoolmanActiveSpoolAsync(string baseUrl, int? spoolId, CancellationToken ct = default);

    /// <summary>
    /// Lists all spools from the Spoolman server connected to this printer's backend.
    /// Uses the backend's proxy endpoint so results reflect the printer-specific Spoolman instance.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>JSON string containing the spool array, or null if unavailable</returns>
    Task<string?> GetSpoolmanSpoolsAsync(string baseUrl, CancellationToken ct = default);
}

/// <summary>
/// Capability interface for status clients whose backends do not have native Spoolman integration.
/// These backends rely on PrintFarmer's database (Printer.CurrentSpoolId) as the source of truth
/// for spool assignments, with spool details fetched from the central Spoolman instance.
/// </summary>
/// <remarks>
/// Moonraker has native Spoolman support and manages spool info through its own API.
/// All other backends (PrusaLink, OctoPrint, SDCP) implement this interface to provide
/// consistent spool tracking across the entire fleet.
/// </remarks>
public interface IManagedSpoolProvider
{
    /// <summary>
    /// Resolves spool info from PrintFarmer's database and central Spoolman instance.
    /// Returns null if no spool is assigned or Spoolman is unavailable.
    /// </summary>
    /// <param name="printer">The printer entity with CurrentSpoolId from the database</param>
    /// <param name="ct">Cancellation token</param>
    Task<PrinterSpoolInfoDto?> GetManagedSpoolInfoAsync(Printer printer, CancellationToken ct);
}

/// <summary>
/// Capability interface for backends that can report actual filament usage after a print completes.
/// Backends implement this to provide real extrusion data from history or job APIs,
/// enabling accurate Spoolman consumption tracking.
/// </summary>
public interface ISupportsFilamentUsageQuery
{
    /// <summary>
    /// Retrieves the actual filament usage (in grams) for the most recently completed print job.
    /// Returns null if usage data is unavailable (e.g., printer doesn't track extrusion).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional authentication credentials for the printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>Filament usage in grams, or null if unavailable</returns>
    Task<double?> GetLastJobFilamentUsageGramsAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability interface for backends that can report actual filament usage broken down by extruder/toolhead.
/// Used for multi-toolhead printers (toolchangers) and MMU printers to accurately track
/// per-spool filament consumption after job completion.
/// </summary>
public interface ISupportsPerExtruderFilamentUsage
{
    /// <summary>
    /// Retrieves actual filament usage per extruder for the most recently completed print job.
    /// Returns a dictionary mapping toolhead index (0-based) to grams used, or null if per-extruder data is unavailable.
    /// Fallback: if per-extruder data isn't available, the caller will use single-total from ISupportsFilamentUsageQuery.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="credential">Optional authentication credentials for the printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>Dictionary mapping toolhead index to grams used, or null if per-extruder data is unavailable</returns>
    Task<Dictionary<int, double>?> GetLastJobFilamentUsagePerExtruderAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backends that support raw G-code execution.
/// Allows sending arbitrary G-code commands directly to the printer firmware.
/// Useful for executing specialized commands not covered by standard capabilities.
/// </summary>
public interface ISupportsGcodeExecution
{
    /// <summary>
    /// Sends a raw G-code command to the printer firmware.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="gcode">The G-code command string to execute (e.g., "M84" to disable motors)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if G-code command was sent and executed successfully, false if send/execution failed</returns>
    Task<bool> SendGcodeAsync(string baseUrl, string gcode, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backends that support filament management operations.
/// Backends implementing this interface can load, unload, and change filament
/// using firmware macros or standard G-code commands (e.g., Klipper macros, M600).
/// </summary>
public interface ISupportsFilamentControl
{
    /// <summary>
    /// Loads filament into the extruder (e.g., Klipper LOAD_FILAMENT macro).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if the load command was sent successfully, false if it failed</returns>
    Task<bool> LoadFilamentAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Unloads filament from the extruder (e.g., Klipper UNLOAD_FILAMENT macro).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if the unload command was sent successfully, false if it failed</returns>
    Task<bool> UnloadFilamentAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Initiates a filament change procedure (e.g., M600 G-code command).
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if the change command was sent successfully, false if it failed</returns>
    Task<bool> ChangeFilamentAsync(string baseUrl, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support connection testing.
/// Provides lightweight connectivity checks to verify a printer endpoint is reachable.
/// </summary>
public interface ISupportsConnectionTest
{
    /// <summary>
    /// Tests connectivity to a printer by sending a lightweight probe request.
    /// </summary>
    /// <param name="baseUrl">The base URL of the printer endpoint</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if the endpoint responded, false otherwise</returns>
    Task<bool> TestConnectionAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Tests connectivity to a printer by sending a lightweight probe request (Uri overload).
    /// </summary>
    /// <param name="baseUrl">The base URI of the printer endpoint</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if the endpoint responded, false otherwise</returns>
    Task<bool> TestConnectionAsync(Uri baseUrl, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backends that support firmware and system restart operations.
/// Used for restarting the printer firmware or associated services.
/// </summary>
public interface ISupportsControlRestart
{
    /// <summary>
    /// Restarts the printer firmware.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if restart command was sent successfully, false if restart failed</returns>
    Task<bool> FirmwareRestartAsync(string baseUrl, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backends that support OctoPrint-specific temperature operations.
/// Extends basic temperature control with OctoPrint's granular hotend/tool targeting and required API key handling.
/// </summary>
public interface ISupportsOctoPrintTemperature
{
    /// <summary>
    /// Sets the target bed temperature (OctoPrint-specific implementation).
    /// </summary>
    /// <param name="baseUrl">The base URL of the OctoPrint server</param>
    /// <param name="apiKey">Required API key for OctoPrint authentication</param>
    /// <param name="bedTemp">Target bed temperature in Celsius</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if temperature was set successfully, false if operation failed</returns>
    Task<bool> SetBedTempAsync(string baseUrl, string apiKey, double bedTemp, CancellationToken ct = default);

    /// <summary>
    /// Sets the target hotend/tool temperature (OctoPrint-specific implementation).
    /// </summary>
    /// <param name="baseUrl">The base URL of the OctoPrint server</param>
    /// <param name="apiKey">Required API key for OctoPrint authentication</param>
    /// <param name="hotendTemp">Target hotend temperature in Celsius</param>
    /// <param name="tool">The tool identifier to target (e.g., "tool0", "tool1"), defaults to "tool0"</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if temperature was set successfully, false if operation failed</returns>
    Task<bool> SetHotendTempAsync(string baseUrl, string apiKey, double hotendTemp, string tool = "tool0", CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support file deletion.
/// Backends implementing this interface can remove files from the printer's storage.
/// </summary>
public interface ISupportsFileDelete
{
    /// <summary>
    /// Deletes a file from the printer's storage.
    /// </summary>
    /// <param name="baseUrl">The base URL of the backend printer server (e.g., http://printer-ip)</param>
    /// <param name="filePath">The path to the file to delete (e.g., "/local/model.gcode")</param>
    /// <param name="credential">Optional credential for authentication if required by the backend</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>True if the file was deleted successfully, false if it failed (e.g., file not found, permission denied)</returns>
    Task<bool> DeleteFileAsync(string baseUrl, string filePath, PrinterCredential? credential = null, CancellationToken ct = default);
}

/// <summary>
/// Standardized printer file information across all backend implementations.
/// Provides consistent file metadata regardless of the backend printer type.
/// </summary>
public class PrinterFileInfo
{
    /// <summary>
    /// The filename of the file (e.g., "model.gcode").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The full path to the file on the printer (e.g., "gcodes/model.gcode").
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The size of the file in bytes, or null if not available.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Unix timestamp (seconds since 1970-01-01 UTC) when the file was last modified, or null if not available.
    /// Backend implementations are responsible for converting from DateTime to Unix timestamp format.
    /// </summary>
    public long? Modified { get; set; }

    /// <summary>
    /// Absolute URL to the file's thumbnail image, or null if no thumbnail available.
    /// Backend implementations are responsible for constructing the complete URL if thumbnails are supported.
    /// </summary>
    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// Standardized printer file metadata across all backend implementations.
/// Provides detailed information extracted from G-code files including estimated print time,
/// layer information, temperature settings, and embedded thumbnails.
/// </summary>
public class PrinterFileMetadata
{
    /// <summary>
    /// The full path to the file on the printer (e.g., "gcodes/model.gcode").
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Estimated total print time in seconds, or null if not available.
    /// </summary>
    public double? PrintTime { get; set; }

    /// <summary>
    /// Layer height in millimeters used for the print, or null if not available.
    /// </summary>
    public double? LayerHeight { get; set; }

    /// <summary>
    /// First layer extrusion temperature in Celsius, or null if not available.
    /// </summary>
    public double? FirstLayerExtrTemp { get; set; }

    /// <summary>
    /// First layer bed temperature in Celsius, or null if not available.
    /// </summary>
    public double? FirstLayerBedTemp { get; set; }

    /// <summary>
    /// Total object height in millimeters, or null if not available.
    /// </summary>
    public double? ObjectHeight { get; set; }

    /// <summary>
    /// Estimated filament used in grams, or null if not available.
    /// </summary>
    public double? ExtrUsedFilament { get; set; }

    /// <summary>
    /// Thumbnail images embedded in the G-code file.
    /// Each tuple contains (Width in pixels, Height in pixels, Relative path to thumbnail file).
    /// The list is empty if no thumbnails are embedded in the file.
    /// </summary>
    public List<(int Width, int Height, string RelativePath)> Thumbnails { get; set; } = new();
}

/// <summary>
/// Standardized printer information across all backend implementations.
/// Provides consistent printer metadata regardless of backend type.
/// Avoids naming conflicts with backend-specific PrinterInfo types.
/// </summary>
public class StandardPrinterInfo
{
    /// <summary>
    /// The configured name of the printer.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The firmware version running on the printer (e.g., "v0.11.0", "Marlin 2.1.1").
    /// </summary>
    public string Firmware { get; set; } = string.Empty;

    /// <summary>
    /// The backend/server software version (e.g., Moonraker/PrusaLink/OctoPrint version).
    /// Optional; may be null or empty if not available.
    /// </summary>
    public string? BackendVersion { get; set; }

    /// <summary>
    /// The backend API version (string form when available).
    /// Optional; may be null or empty if not available.
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// The printer model or hardware type (e.g., "Prusa i3 MK3S+", "Voron 2.4").
    /// </summary>
    public string Model { get; set; } = string.Empty;
}
