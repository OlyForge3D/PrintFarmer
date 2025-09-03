namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Interface for Moonraker client providing communication with Moonraker/Klipper 3D printer firmware.
/// Supports printer status monitoring, job control, file management, history tracking, and Spoolman integration.
/// </summary>
public interface IMoonrakerClient
{
    #region Status and Job Information

    /// <summary>
    /// Gets the basic status information from a Moonraker printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server (e.g., http://printer-ip)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing printer status information including online status and state</returns>
    Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default);
    // Overload to accept Uri for analyzer CA1054 friendliness
    Task<PrinterStatus> GetStatusAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets the printer information including hostname from a Moonraker printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server (e.g., http://printer-ip)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing printer information including hostname, or null if not available</returns>
    Task<MoonrakerPrinterInfo?> GetPrinterInfoAsync(string baseUrl, CancellationToken ct = default);
    Task<MoonrakerPrinterInfo?> GetPrinterInfoAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets the current print job information from a Moonraker printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing current job information, or null if no job is active</returns>
    Task<PrinterJob?> GetJobAsync(string baseUrl, CancellationToken ct = default);
    Task<PrinterJob?> GetJobAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets comprehensive status information combining printer state, job progress, and position data.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing detailed printer status including temperatures, position, and job progress</returns>
    Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default);
    Task<PrinterCompositeStatus> GetCompositeStatusAsync(Uri baseUrl, CancellationToken ct = default);

    #endregion

    #region Camera Operations

    /// <summary>
    /// Captures a snapshot from the printer's camera.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the camera snapshot as byte array, or null if no camera is available</returns>
    Task<byte[]?> GetCameraSnapshotAsync(string baseUrl, CancellationToken ct = default);
    Task<byte[]?> GetCameraSnapshotAsync(Uri baseUrl, CancellationToken ct = default);

    #endregion

    #region Printer Control Operations

    /// <summary>
    /// Homes all axes of the printer (X, Y, and Z).
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the home command was successfully sent</returns>
    Task<bool> SendHomeAsync(string baseUrl, CancellationToken ct = default);
    Task<bool> SendHomeAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Homes only the X and Y axes of the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the XY home command was successfully sent</returns>
    Task<bool> HomeXYAsync(string baseUrl, CancellationToken ct = default);
    Task<bool> HomeXYAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Homes only the Z axis of the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the Z home command was successfully sent</returns>
    Task<bool> HomeZAsync(string baseUrl, CancellationToken ct = default);
    Task<bool> HomeZAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Sets the target temperatures for the hotend and/or bed.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="hotend">Target hotend temperature in Celsius, or null to leave unchanged</param>
    /// <param name="bed">Target bed temperature in Celsius, or null to leave unchanged</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the temperature commands were successfully sent</returns>
    Task<bool> SetTempsAsync(string baseUrl, double? hotend = null, double? bed = null, CancellationToken ct = default);
    Task<bool> SetTempsAsync(Uri baseUrl, double? hotend = null, double? bed = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the printer head by relative distances.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="x">Relative X movement in mm, or null for no movement</param>
    /// <param name="y">Relative Y movement in mm, or null for no movement</param>
    /// <param name="z">Relative Z movement in mm, or null for no movement</param>
    /// <param name="f">Feed rate in mm/min, or null to use default</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the move command was successfully sent</returns>
    Task<bool> MoveAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default);
    Task<bool> MoveAsync(Uri baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the printer head to absolute positions.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="x">Absolute X position in mm, or null to leave unchanged</param>
    /// <param name="y">Absolute Y position in mm, or null to leave unchanged</param>
    /// <param name="z">Absolute Z position in mm, or null to leave unchanged</param>
    /// <param name="f">Feed rate in mm/min, or null to use default</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the move command was successfully sent</returns>
    Task<bool> MoveToAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default);
    Task<bool> MoveToAsync(Uri baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default);

    #endregion

    #region Print Job Control

    /// <summary>
    /// Pauses the current print job.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the pause command was successfully sent</returns>
    Task<bool> PauseAsync(string baseUrl, CancellationToken ct = default);
    Task<bool> PauseAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Resumes a paused print job.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the resume command was successfully sent</returns>
    Task<bool> ResumeAsync(string baseUrl, CancellationToken ct = default);
    Task<bool> ResumeAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Performs an emergency stop, immediately halting all printer operations.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the emergency stop command was successfully sent</returns>
    Task<bool> EmergencyStopAsync(string baseUrl, CancellationToken ct = default);
    Task<bool> EmergencyStopAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Starts printing a G-code file by name.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="fileName">The name of the G-code file to print</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the print start command was successfully sent</returns>
    Task<bool> StartPrintAsync(string baseUrl, string fileName, CancellationToken ct = default);
    Task<bool> StartPrintAsync(Uri baseUrl, string fileName, CancellationToken ct = default);

    #endregion

    #region File Operations

    /// <summary>
    /// Gets a simple list of G-code file names available on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing an array of G-code file names</returns>
    Task<string[]> GetFileListAsync(string baseUrl, CancellationToken ct = default);
    Task<string[]> GetFileListAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets information about available file storage roots on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing file root information</returns>
    Task<FileRoot[]> GetFileRootsAsync(string baseUrl, CancellationToken ct = default);
    Task<FileRoot[]> GetFileRootsAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about files and directories at a specific path.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="path">The directory path to query</param>
    /// <param name="extended">Whether to include extended file information</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing directory information, or null if the directory doesn't exist</returns>
    Task<DirectoryInfo?> GetDirectoryAsync(string baseUrl, string path, bool extended = false, CancellationToken ct = default);
    Task<DirectoryInfo?> GetDirectoryAsync(Uri baseUrl, string path, bool extended = false, CancellationToken ct = default);

    /// <summary>
    /// Creates a new directory at the specified path.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="path">The path where the directory should be created</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the create directory response, or null if creation failed</returns>
    Task<DirectoryCreateResponse?> CreateDirectoryAsync(string baseUrl, string path, CancellationToken ct = default);
    Task<DirectoryCreateResponse?> CreateDirectoryAsync(Uri baseUrl, string path, CancellationToken ct = default);

    /// <summary>
    /// Deletes a file or directory at the specified path.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="path">The path of the file or directory to delete</param>
    /// <param name="force">Whether to force deletion of non-empty directories</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the deletion was successful</returns>
    Task<bool> DeleteFileOrDirectoryAsync(string baseUrl, string path, bool force = false, CancellationToken ct = default);
    Task<bool> DeleteFileOrDirectoryAsync(Uri baseUrl, string path, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Moves a file from one location to another.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="source">The source file path</param>
    /// <param name="dest">The destination file path</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the move operation was successful</returns>
    Task<bool> MoveFileAsync(string baseUrl, string source, string dest, CancellationToken ct = default);
    Task<bool> MoveFileAsync(Uri baseUrl, string source, string dest, CancellationToken ct = default);

    /// <summary>
    /// Copies a file from one location to another.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="source">The source file path</param>
    /// <param name="dest">The destination file path</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the copy operation was successful</returns>
    Task<bool> CopyFileAsync(string baseUrl, string source, string dest, CancellationToken ct = default);
    Task<bool> CopyFileAsync(Uri baseUrl, string source, string dest, CancellationToken ct = default);

    /// <summary>
    /// Deletes a specific file.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="path">The path of the file to delete</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the file deletion was successful</returns>
    Task<bool> DeleteFileAsync(string baseUrl, string path, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(Uri baseUrl, string path, CancellationToken ct = default);

    /// <summary>
    /// Gets a stream for reading file contents.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filename">The name of the file to read</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing a stream for reading the file, or null if the file doesn't exist</returns>
    Task<Stream?> GetFileStreamAsync(string baseUrl, string filename, CancellationToken ct = default);
    Task<Stream?> GetFileStreamAsync(Uri baseUrl, string filename, CancellationToken ct = default);

    #endregion

    #region File Metadata and Content

    /// <summary>
    /// Gets metadata for a G-code file including print time estimates, layer information, and slicer settings.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filename">The name of the G-code file</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing G-code metadata, or null if metadata is not available</returns>
    Task<GCodeMetadata?> GetFileMetadataAsync(string baseUrl, string filename, CancellationToken ct = default);
    Task<GCodeMetadata?> GetFileMetadataAsync(Uri baseUrl, string filename, CancellationToken ct = default);

    /// <summary>
    /// Starts a metadata scan for a G-code file to extract print information.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filename">The name of the G-code file to scan</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the metadata scan was successfully started</returns>
    Task<bool> StartMetadataScanAsync(string baseUrl, string filename, CancellationToken ct = default);
    Task<bool> StartMetadataScanAsync(Uri baseUrl, string filename, CancellationToken ct = default);

    /// <summary>
    /// Gets a thumbnail image embedded in a G-code file.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filename">The name of the G-code file</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the thumbnail image as byte array, or null if no thumbnail is available</returns>
    Task<byte[]?> GetFileThumbnailAsync(string baseUrl, string filename, CancellationToken ct = default);
    Task<byte[]?> GetFileThumbnailAsync(Uri baseUrl, string filename, CancellationToken ct = default);

    /// <summary>
    /// Downloads the complete contents of a file.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filename">The name of the file to download</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the file contents as byte array, or null if the file doesn't exist</returns>
    Task<byte[]?> DownloadFileAsync(string baseUrl, string filename, CancellationToken ct = default);
    Task<byte[]?> DownloadFileAsync(Uri baseUrl, string filename, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed file information including metadata for files in a specific directory.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="root">The root directory to search (default: "gcodes")</param>
    /// <param name="path">Optional subdirectory path within the root</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing detailed file information</returns>
    Task<MoonrakerFileInfo[]> GetDetailedFileListAsync(string baseUrl, string root = "gcodes", string? path = null, CancellationToken ct = default);
    Task<MoonrakerFileInfo[]> GetDetailedFileListAsync(Uri baseUrl, string root = "gcodes", string? path = null, CancellationToken ct = default);

    #endregion

    #region File Uploads

    /// <summary>
    /// Uploads a G-code file to the printer's storage.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="fileName">The name to save the file as</param>
    /// <param name="fileContent">Stream containing the G-code file content</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the upload was successful</returns>
    Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, CancellationToken ct = default);
    Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, CancellationToken ct = default);

    /// <summary>
    /// Uploads a file to a specific storage root on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="root">The storage root (e.g., "gcodes", "config")</param>
    /// <param name="filename">The name to save the file as</param>
    /// <param name="content">Stream containing the file content</param>
    /// <param name="print">Whether to start printing the file immediately after upload</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the upload response, or null if upload failed</returns>
    Task<FileUploadResponse?> UploadFileAsync(string baseUrl, string root, string filename, Stream content,
        bool print = false, CancellationToken ct = default);
    Task<FileUploadResponse?> UploadFileAsync(Uri baseUrl, string root, string filename, Stream content,
        bool print = false, CancellationToken ct = default);

    /// <summary>
    /// Uploads a file using a full path specification.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="path">The full path where the file should be saved</param>
    /// <param name="content">Stream containing the file content</param>
    /// <param name="print">Whether to start printing the file immediately after upload</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the upload response, or null if upload failed</returns>
    Task<FileUploadResponse?> UploadFileWithPathAsync(string baseUrl, string path, Stream content,
        bool print = false, CancellationToken ct = default);
    Task<FileUploadResponse?> UploadFileWithPathAsync(Uri baseUrl, string path, Stream content,
        bool print = false, CancellationToken ct = default);

    #endregion

    #region History Operations

    /// <summary>
    /// Gets a list of completed print jobs from the printer's history.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="limit">Maximum number of jobs to return</param>
    /// <param name="start">Starting index for pagination</param>
    /// <param name="since">Only return jobs completed after this date</param>
    /// <param name="before">Only return jobs completed before this date</param>
    /// <param name="order">Sort order for the results</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the history list response, or null if history is unavailable</returns>
    Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, DateTime? since = null, DateTime? before = null, string? order = null, CancellationToken ct = default);
    Task<HistoryListResponse?> GetHistoryListAsync(Uri baseUrl, int? limit = null, int? start = null, DateTime? since = null, DateTime? before = null, string? order = null, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about a specific print job from history.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="jobId">The unique identifier of the print job</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing detailed job information, or null if the job doesn't exist</returns>
    Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, CancellationToken ct = default);
    Task<HistoryJob?> GetHistoryJobAsync(Uri baseUrl, string jobId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a specific print job from the history.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="jobId">The unique identifier of the print job to delete</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the job deletion was successful</returns>
    Task<bool> DeleteHistoryJobAsync(string baseUrl, string jobId, CancellationToken ct = default);
    Task<bool> DeleteHistoryJobAsync(Uri baseUrl, string jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets aggregate statistics for all print jobs in history.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing history totals and statistics, or null if unavailable</returns>
    Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, CancellationToken ct = default);
    Task<HistoryTotals?> GetHistoryTotalsAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Resets all history totals and statistics to zero.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the history reset was successful</returns>
    Task<bool> ResetHistoryTotalsAsync(string baseUrl, CancellationToken ct = default);
    Task<bool> ResetHistoryTotalsAsync(Uri baseUrl, CancellationToken ct = default);

    #endregion

    #region Spoolman Integration

    /// <summary>
    /// Gets the current status of Spoolman integration on the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing Spoolman status information, or null if Spoolman is not configured</returns>
    Task<SpoolmanStatus?> GetSpoolmanStatusAsync(string baseUrl, CancellationToken ct = default);
    Task<SpoolmanStatus?> GetSpoolmanStatusAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets the ID of the currently active filament spool.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the active spool ID, or null if no spool is active</returns>
    Task<int?> GetSpoolmanActiveSpoolAsync(string baseUrl, CancellationToken ct = default);
    Task<int?> GetSpoolmanActiveSpoolAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Sets the active filament spool for the printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="spoolId">The ID of the spool to activate, or null to deactivate current spool</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the spool activation was successful</returns>
    Task<bool> SetSpoolmanActiveSpoolAsync(string baseUrl, int? spoolId, CancellationToken ct = default);
    Task<bool> SetSpoolmanActiveSpoolAsync(Uri baseUrl, int? spoolId, CancellationToken ct = default);

    /// <summary>
    /// Makes a proxy request to the Spoolman server through Moonraker.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="method">HTTP method for the request (GET, POST, PUT, DELETE)</param>
    /// <param name="path">API path to call on the Spoolman server</param>
    /// <param name="query">Optional query string parameters</param>
    /// <param name="body">Optional request body for POST/PUT requests</param>
    /// <param name="useV2Response">Whether to use v2 response format</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the Spoolman API response as JSON string, or null if the request failed</returns>
    Task<string?> SpoolmanProxyRequestAsync(string baseUrl, string method, string path,
        string? query = null, object? body = null, bool useV2Response = false, CancellationToken ct = default);
    Task<string?> SpoolmanProxyRequestAsync(Uri baseUrl, string method, string path,
        string? query = null, object? body = null, bool useV2Response = false, CancellationToken ct = default);

    #endregion

    #region Spoolman Spool Operations

    /// <summary>
    /// Gets a list of all filament spools from Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the spools list as JSON string, or null if the request failed</returns>
    Task<string?> GetSpoolmanSpoolsAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> GetSpoolmanSpoolsAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about a specific filament spool.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="spoolId">The unique identifier of the spool</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing spool information as JSON string, or null if the spool doesn't exist</returns>
    Task<string?> GetSpoolmanSpoolByIdAsync(string baseUrl, int spoolId, CancellationToken ct = default);
    Task<string?> GetSpoolmanSpoolByIdAsync(Uri baseUrl, int spoolId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new filament spool in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="spoolData">Object containing spool information (filament, weight, color, etc.)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the created spool information as JSON string, or null if creation failed</returns>
    Task<string?> CreateSpoolmanSpoolAsync(string baseUrl, object spoolData, CancellationToken ct = default);
    Task<string?> CreateSpoolmanSpoolAsync(Uri baseUrl, object spoolData, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing filament spool in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="spoolId">The unique identifier of the spool to update</param>
    /// <param name="spoolData">Object containing updated spool information</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the updated spool information as JSON string, or null if update failed</returns>
    Task<string?> UpdateSpoolmanSpoolAsync(string baseUrl, int spoolId, object spoolData, CancellationToken ct = default);
    Task<string?> UpdateSpoolmanSpoolAsync(Uri baseUrl, int spoolId, object spoolData, CancellationToken ct = default);

    /// <summary>
    /// Deletes a filament spool from Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="spoolId">The unique identifier of the spool to delete</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the spool deletion was successful</returns>
    Task<bool> DeleteSpoolmanSpoolAsync(string baseUrl, int spoolId, CancellationToken ct = default);
    Task<bool> DeleteSpoolmanSpoolAsync(Uri baseUrl, int spoolId, CancellationToken ct = default);

    #endregion

    #region Spoolman Filament Operations

    /// <summary>
    /// Gets a list of all filament types from Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the filaments list as JSON string, or null if the request failed</returns>
    Task<string?> GetSpoolmanFilamentsAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> GetSpoolmanFilamentsAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about a specific filament type.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filamentId">The unique identifier of the filament</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing filament information as JSON string, or null if the filament doesn't exist</returns>
    Task<string?> GetSpoolmanFilamentByIdAsync(string baseUrl, int filamentId, CancellationToken ct = default);
    Task<string?> GetSpoolmanFilamentByIdAsync(Uri baseUrl, int filamentId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new filament type in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filamentData">Object containing filament information (name, material, vendor, etc.)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the created filament information as JSON string, or null if creation failed</returns>
    Task<string?> CreateSpoolmanFilamentAsync(string baseUrl, object filamentData, CancellationToken ct = default);
    Task<string?> CreateSpoolmanFilamentAsync(Uri baseUrl, object filamentData, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing filament type in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filamentId">The unique identifier of the filament to update</param>
    /// <param name="filamentData">Object containing updated filament information</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the updated filament information as JSON string, or null if update failed</returns>
    Task<string?> UpdateSpoolmanFilamentAsync(string baseUrl, int filamentId, object filamentData, CancellationToken ct = default);
    Task<string?> UpdateSpoolmanFilamentAsync(Uri baseUrl, int filamentId, object filamentData, CancellationToken ct = default);

    /// <summary>
    /// Deletes a filament type from Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="filamentId">The unique identifier of the filament to delete</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the filament deletion was successful</returns>
    Task<bool> DeleteSpoolmanFilamentAsync(string baseUrl, int filamentId, CancellationToken ct = default);
    Task<bool> DeleteSpoolmanFilamentAsync(Uri baseUrl, int filamentId, CancellationToken ct = default);

    #endregion

    #region Spoolman Vendor Operations

    /// <summary>
    /// Gets a list of all filament vendors from Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the vendors list as JSON string, or null if the request failed</returns>
    Task<string?> GetSpoolmanVendorsAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> GetSpoolmanVendorsAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about a specific filament vendor.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="vendorId">The unique identifier of the vendor</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing vendor information as JSON string, or null if the vendor doesn't exist</returns>
    Task<string?> GetSpoolmanVendorByIdAsync(string baseUrl, int vendorId, CancellationToken ct = default);
    Task<string?> GetSpoolmanVendorByIdAsync(Uri baseUrl, int vendorId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new filament vendor in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="vendorData">Object containing vendor information (name, contact info, etc.)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the created vendor information as JSON string, or null if creation failed</returns>
    Task<string?> CreateSpoolmanVendorAsync(string baseUrl, object vendorData, CancellationToken ct = default);
    Task<string?> CreateSpoolmanVendorAsync(Uri baseUrl, object vendorData, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing filament vendor in Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="vendorId">The unique identifier of the vendor to update</param>
    /// <param name="vendorData">Object containing updated vendor information</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the updated vendor information as JSON string, or null if update failed</returns>
    Task<string?> UpdateSpoolmanVendorAsync(string baseUrl, int vendorId, object vendorData, CancellationToken ct = default);
    Task<string?> UpdateSpoolmanVendorAsync(Uri baseUrl, int vendorId, object vendorData, CancellationToken ct = default);

    /// <summary>
    /// Deletes a filament vendor from Spoolman.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="vendorId">The unique identifier of the vendor to delete</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the vendor deletion was successful</returns>
    Task<bool> DeleteSpoolmanVendorAsync(string baseUrl, int vendorId, CancellationToken ct = default);
    Task<bool> DeleteSpoolmanVendorAsync(Uri baseUrl, int vendorId, CancellationToken ct = default);

    #endregion

    #region Spoolman Utility and Advanced Operations

    /// <summary>
    /// Records filament usage for the currently active spool.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="length">Length of filament used in millimeters</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the filament usage was successfully recorded</returns>
    Task<bool> UseSpoolmanFilamentAsync(string baseUrl, double length, CancellationToken ct = default);
    Task<bool> UseSpoolmanFilamentAsync(Uri baseUrl, double length, CancellationToken ct = default);

    /// <summary>
    /// Gets general information about the Spoolman server.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing Spoolman server information as JSON string, or null if unavailable</returns>
    Task<string?> GetSpoolmanInfoAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> GetSpoolmanInfoAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Checks the health status of the Spoolman server.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing Spoolman health information as JSON string, or null if health check failed</returns>
    Task<string?> GetSpoolmanHealthAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> GetSpoolmanHealthAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Searches for spools matching specific criteria.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="query">Search query string</param>
    /// <param name="allowArchived">Whether to include archived spools in results</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip for pagination</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing search results as JSON string, or null if search failed</returns>
    Task<string?> SearchSpoolmanSpoolsAsync(string baseUrl, string? query = null,
        bool? allowArchived = null, int? limit = null, int? offset = null, CancellationToken ct = default);
    Task<string?> SearchSpoolmanSpoolsAsync(Uri baseUrl, string? query = null,
        bool? allowArchived = null, int? limit = null, int? offset = null, CancellationToken ct = default);

    /// <summary>
    /// Searches for filaments matching specific criteria.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="query">Search query string</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip for pagination</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing search results as JSON string, or null if search failed</returns>
    Task<string?> SearchSpoolmanFilamentsAsync(string baseUrl, string? query = null,
        int? limit = null, int? offset = null, CancellationToken ct = default);
    Task<string?> SearchSpoolmanFilamentsAsync(Uri baseUrl, string? query = null,
        int? limit = null, int? offset = null, CancellationToken ct = default);

    /// <summary>
    /// Archives or unarchives a filament spool.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="spoolId">The unique identifier of the spool</param>
    /// <param name="archived">True to archive the spool, false to unarchive</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the archive operation was successful</returns>
    Task<bool> ArchiveSpoolmanSpoolAsync(string baseUrl, int spoolId, bool archived = true, CancellationToken ct = default);
    Task<bool> ArchiveSpoolmanSpoolAsync(Uri baseUrl, int spoolId, bool archived = true, CancellationToken ct = default);

    /// <summary>
    /// Gets aggregate statistics from Spoolman about filament usage.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing statistics as JSON string, or null if unavailable</returns>
    Task<string?> GetSpoolmanStatsAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> GetSpoolmanStatsAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Creates a backup of the Spoolman database.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing backup information as JSON string, or null if backup failed</returns>
    Task<string?> BackupSpoolmanAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> BackupSpoolmanAsync(Uri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets information about available Spoolman integrations and their status.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Moonraker server</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing integration information as JSON string, or null if unavailable</returns>
    Task<string?> GetSpoolmanIntegrationsAsync(string baseUrl, CancellationToken ct = default);
    Task<string?> GetSpoolmanIntegrationsAsync(Uri baseUrl, CancellationToken ct = default);

    #endregion
}
