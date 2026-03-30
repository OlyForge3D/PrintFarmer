using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Service interface for printer management and operations.
/// Provides CRUD operations, status retrieval, file management, printer control operations,
/// and integration with multiple printer backend types (Moonraker, PrusaLink, OctoPrint, SDCP).
/// Encapsulates all high-level printer orchestration and business logic.
/// </summary>
public interface IPrintersService
{
    /// <summary>
    /// Retrieves all printers without related entities.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all printers in the database</returns>
    Task<List<Printer>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves all printers with related entities loaded (includes manufacturer, model, etc.).
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of printers with eagerly loaded relationships</returns>
    Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct);

    /// <summary>
    /// Finds a single printer by ID without loading relationships.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The printer if found, null otherwise</returns>
    Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a printer by ID with all related entities eagerly loaded.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The printer with relationships loaded, or null if not found</returns>
    Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Retrieves all printers with Toolheads included, with tracking enabled for template updates.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of all printer entities with Toolheads, suitable for template application</returns>
    Task<List<Printer>> GetAllForTemplateUpdateAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves a single printer with Toolheads included, with tracking enabled for template updates.
    /// </summary>
    /// <param name="id">The printer ID (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Printer entity with Toolheads if found; otherwise null</returns>
    Task<Printer?> FindByIdForTemplateUpdateAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Adds a new printer to the database.
    /// </summary>
    /// <param name="p">The printer entity to add</param>
    /// <param name="ct">Cancellation token</param>
    Task AddAsync(Printer p, CancellationToken ct);

    /// <summary>
    /// Removes a printer from the database.
    /// </summary>
    /// <param name="p">The printer entity to remove</param>
    /// <param name="ct">Cancellation token</param>
    Task RemoveAsync(Printer p, CancellationToken ct);

    /// <summary>
    /// Saves all pending changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves printers for export operations, optionally filtered by IDs.
    /// </summary>
    /// <param name="ids">Array of printer IDs to export, or null to export all</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of printers ready for export</returns>
    Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct);

    /// <summary>
    /// Checks if a printer exists with the given name or server URL.
    /// Used to prevent duplicate printer configurations.
    /// </summary>
    /// <param name="name">The printer name to check</param>
    /// <param name="serverUrl">The server URL to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if a printer exists with this name or URL, false otherwise</returns>
    Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct);

    /// <summary>
    /// Finds a printer by its ServerUrl.
    /// </summary>
    /// <param name="serverUrl">The server URL to search for</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The printer with matching ServerUrl, or null if not found</returns>
    Task<Printer?> FindByServerUrlAsync(string serverUrl, CancellationToken ct);

    /// <summary>
    /// Retrieves all printers with current status information (online/offline, printer state).
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of printer DTOs with live status</returns>
    Task<PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves the current status of a specific printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Printer status DTO with current state information</returns>
    Task<PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Retrieves a single printer as a DTO for API responses.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Printer DTO with configured properties</returns>
    Task<PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Retrieves camera URLs for all printers from configured storage.
    /// Does NOT make external API calls - uses cached values.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of printer camera URLs</returns>
    Task<PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves all printers in a fast DTO format for list operations.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of fast printer DTOs</returns>
    Task<PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves all printers with complete status and configuration data.
    /// Replaces GetAllFastDtosAsync for comprehensive printer data in new API endpoints.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of complete printer DTOs with live status merged in</returns>
    Task<CompletePrinterDto[]> GetAllCompleteDtosAsync(CancellationToken ct);

    /// <summary>
    /// Builds a CSV export of printer data.
    /// </summary>
    /// <param name="ids">Array of printer IDs to export, or null to export all</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CSV data as byte array</returns>
    Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct);

    /// <summary>
    /// Builds a JSON export of printer data.
    /// </summary>
    /// <param name="ids">Array of printer IDs to export, or null to export all</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>JSON data as byte array</returns>
    Task<byte[]> BuildExportJsonAsync(Guid[]? ids, CancellationToken ct);

    /// <summary>
    /// Retrieves printers with their supported capabilities grouped by backend type.
    /// </summary>
    /// <param name="ids">Array of printer IDs, or null to include all</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of printers with capability information</returns>
    Task<PrinterWithCapabilitiesDto[]> GetPrintersWithCapabilitiesDtosAsync(Guid[]? ids, CancellationToken ct);

    /// <summary>
    /// Creates a new printer from a DTO, resolving manufacturer/model and persisting to database.
    /// </summary>
    /// <param name="dto">The printer creation DTO with configuration</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The created printer as a DTO</returns>
    Task<PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterFromDiscoveryDto dto, CancellationToken ct);

    /// <summary>
    /// Applies template defaults from the PrinterModel to an existing printer.
    /// Copies hardware specifications (build volume, max temps, supported materials, etc.)
    /// from the associated PrinterModel to the printer.
    /// </summary>
    /// <param name="printer">The printer entity to update (must include Toolheads if updating toolhead properties)</param>
    /// <param name="forceOverwrite">If true, overwrites all values from template. If false, only fills in null/unset values.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if any values were updated, false if no changes were made</returns>
    Task<bool> ApplyModelTemplateAsync(Printer printer, bool forceOverwrite, CancellationToken ct);

    /// <summary>
    /// Resolves a printer hostname to its base URL and IP address.
    /// </summary>
    /// <param name="serverUrl">The server URL or hostname to resolve</param>
    /// <param name="backend">The printer backend type</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Response containing normalized URL and resolved IP</returns>
    Task<ResolveHostnameResponse> ResolveHostnameAsync(string serverUrl, PrinterBackend backend, CancellationToken ct);

    /// <summary>
    /// Extracts a thumbnail URL from printer metadata.
    /// </summary>
    /// <param name="metadata">Metadata dictionary from printer backend</param>
    /// <param name="printerServerUrl">The printer server URL for constructing absolute paths</param>
    /// <returns>Thumbnail URL if available, null otherwise</returns>
    string? ExtractThumbnailUrl(Dictionary<string, object> metadata, string printerServerUrl);

    /// <summary>
    /// Retrieves a camera snapshot image from a printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Image data as byte array, or null if no camera or retrieval fails</returns>
    Task<byte[]?> GetCameraSnapshotAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Retrieves camera stream and snapshot URLs for a printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Tuple of (streamUrl, snapshotUrl), either or both may be null</returns>
    Task<(string? StreamUrl, string? SnapshotUrl)> GetCameraUrlsForPrinterAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Retrieves the history of print jobs for a printer.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="limit">Maximum number of jobs to return, or null for no limit</param>
    /// <param name="start">Starting index for pagination, or null to start from beginning</param>
    /// <param name="since">Return jobs after this date, or null for no date filter</param>
    /// <param name="before">Return jobs before this date, or null for no date filter</param>
    /// <param name="order">Sort order for results (asc/desc), or null for default</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Paginated history list with job records and total count</returns>
    Task<HistoryListResponse> GetHistoryListAsync(Guid printerId, int? limit, int? start, DateTime? since, DateTime? before, string? order, CancellationToken ct);

    /// <summary>
    /// Retrieves details for a specific print job from printer history.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="jobId">The backend-specific job identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Complete job details including print time, filament used, and outcome</returns>
    Task<HistoryJob> GetHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct);

    /// <summary>
    /// Retrieves aggregate statistics for all print jobs in printer history.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Aggregated totals including total jobs, time, filament, and longest job</returns>
    Task<HistoryTotals> GetHistoryTotalsAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Deletes a specific print job from the printer's history.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="jobId">The backend-specific job identifier to delete</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if deletion succeeded, false if job not found or deletion failed</returns>
    Task<bool> DeleteHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct);

    /// <summary>
    /// Enables the camera for a printer (if supported by backend).
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if camera enabled successfully, false if not supported or failed</returns>
    Task<bool> EnableCameraAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Disables the camera for a printer (if supported by backend).
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if camera disabled successfully, false if not supported or failed</returns>
    Task<bool> DisableCameraAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Sends a home command for all axes (X, Y, Z).
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if home command sent successfully, false if operation not supported</returns>
    Task<bool> SendHomeAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Sends a home command for X and Y axes only (horizontal movement).
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if home command sent successfully, false if operation not supported</returns>
    Task<bool> HomeXYAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Sends a home command for Z axis only (vertical movement).
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if home command sent successfully, false if operation not supported</returns>
    Task<bool> HomeZAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Sets target temperatures for hotend and/or bed heaters.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="hotend">Target hotend temperature in Celsius, or null to leave unchanged</param>
    /// <param name="bed">Target bed temperature in Celsius, or null to leave unchanged</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if temperature command succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> SetTempsAsync(Guid id, double? hotend, double? bed, CancellationToken ct);

    /// <summary>
    /// Moves the print head by specified offsets from current position (relative movement).
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="x">X-axis offset in millimeters, or null to skip X movement</param>
    /// <param name="y">Y-axis offset in millimeters, or null to skip Y movement</param>
    /// <param name="z">Z-axis offset in millimeters, or null to skip Z movement</param>
    /// <param name="f">Feedrate in mm/min, or null to use backend default</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if movement succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> MoveAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct);

    /// <summary>
    /// Moves the print head to specified absolute position coordinates.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="x">Target X-axis position in millimeters, or null to skip X movement</param>
    /// <param name="y">Target Y-axis position in millimeters, or null to skip Y movement</param>
    /// <param name="z">Target Z-axis position in millimeters, or null to skip Z movement</param>
    /// <param name="f">Feedrate in mm/min, or null to use backend default</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if positioning succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> MoveToAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct);

    /// <summary>
    /// Pauses the currently running print job without canceling it.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if pause succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> PauseAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Resumes a paused print job from where it was paused.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if resume succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> ResumeAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gracefully cancels the currently running print job.
    /// Uses CANCEL_PRINT macro on Moonraker, stop endpoint on PrusaLink, cancel command on OctoPrint.
    /// Heaters will cool down and the print cannot be resumed after cancellation.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if cancel succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> CancelPrintAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Immediately stops and cancels the currently running print job using emergency stop (M112).
    /// This is more aggressive than CancelPrintAsync - use only in emergencies.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if stop succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> EmergencyStopAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Reboots the printer's microcontroller (MCU).
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if restart succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> FirmwareRestartAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Disables all stepper motors on the printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if motors disabled successfully, false if backend unavailable or unsupported</returns>
    Task<bool> DisableMotorsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Sends an arbitrary G-code command to the printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="gcode">The G-code command string to execute (e.g., "LOAD_FILAMENT", "M600")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if command sent successfully, false if backend unavailable or unsupported</returns>
    Task<bool> SendGcodeAsync(Guid id, string gcode, CancellationToken ct);

    /// <summary>
    /// Loads filament into the extruder via the backend's filament control capability.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CommandResult with success/failure and descriptive message</returns>
    Task<CommandResult> LoadFilamentAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Unloads filament from the extruder via the backend's filament control capability.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CommandResult with success/failure and descriptive message</returns>
    Task<CommandResult> UnloadFilamentAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Initiates a filament change procedure via the backend's filament control capability.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CommandResult with success/failure and descriptive message</returns>
    Task<CommandResult> ChangeFilamentAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Sets or clears the active Spoolman spool for a printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="spoolId">The spool ID to activate, or null to clear</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CommandResult with success/failure and descriptive message</returns>
    Task<CommandResult> SetActiveSpoolAsync(Guid id, int? spoolId, CancellationToken ct);

    /// <summary>
    /// Lists available spools from the Spoolman instance connected to a printer's backend.
    /// Routes through the printer's backend proxy so each printer can use its own Spoolman server.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Parsed spool DTOs, or null if the printer is not found or does not support Spoolman</returns>
    Task<IReadOnlyList<SpoolmanSpoolDto>?> ListPrinterSpoolsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Assigns a Spoolman spool to a specific toolhead (by index) on a printer.
    /// Fetches spool details from Spoolman to populate material and color information.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="toolheadIndex">Zero-based index of the toolhead (T0, T1, T2, etc.)</param>
    /// <param name="spoolId">The Spoolman spool ID to assign</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CommandResult with success/failure and descriptive message</returns>
    Task<CommandResult> SetToolheadSpoolAsync(Guid id, int toolheadIndex, int spoolId, CancellationToken ct);

    /// <summary>
    /// Clears the spool assignment from a specific toolhead (by index) on a printer.
    /// Removes the spool ID, material, and color information.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="toolheadIndex">Zero-based index of the toolhead (T0, T1, T2, etc.)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CommandResult with success/failure and descriptive message</returns>
    Task<CommandResult> ClearToolheadSpoolAsync(Guid id, int toolheadIndex, CancellationToken ct);

    /// <summary>
    /// Starts printing a gcode file that exists on the printer's storage.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="filename">Filename of gcode file on printer (backend-specific path format)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if print started successfully, false if backend unavailable or file not found</returns>
    Task<bool> StartPrintFromFileAsync(Guid id, string filename, CancellationToken ct);

    /// <summary>
    /// Deletes a gcode file from the printer's storage.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="filename">The filename to delete (backend-specific path format)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if deletion succeeded, false if not supported or file not found</returns>
    Task<bool> DeletePrinterFileAsync(Guid id, string filename, CancellationToken ct);

    /// <summary>
    /// Uploads a gcode file to the printer's storage.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="filename">The desired filename on printer storage (backend-specific path format)</param>
    /// <param name="stream">The file stream to upload (not closed by this method)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if upload succeeded, false if backend unavailable or unsupported</returns>
    Task<bool> UploadGcodeAsync(Guid id, string filename, Stream stream, CancellationToken ct);

    /// <summary>
    /// Uploads a gcode file to the printer and starts printing it in a single backend operation.
    /// Each backend handles any required delays, path resolution, or protocol-specific steps
    /// between upload and print start.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="filename">The desired filename on printer storage</param>
    /// <param name="stream">The file stream to upload (not closed by this method)</param>
    /// <param name="progress">Optional progress reporter for stage transitions (Uploading → Processing → StartingPrint → Completed/Failed)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>An <see cref="UploadAndPrintResult"/> indicating success or which stage failed, or a failure result if the capability is not supported</returns>
    Task<UploadAndPrintResult> UploadAndStartPrintAsync(Guid id, string filename, Stream stream, IProgress<UploadAndPrintStage>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the list of gcode files stored on the printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of file DTOs representing files on printer storage, or empty array if none found</returns>
    Task<PrinterFileDto[]> GetFileListAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Downloads a gcode file from the printer's storage.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="filename">The filename to download (backend-specific path format)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>File contents as byte array, or null if download failed or not supported</returns>
    Task<byte[]?> DownloadPrinterFileAsync(Guid id, string filename, CancellationToken ct);

    /// <summary>
    /// Retrieves the current print job status for a printer.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Current print job status DTO, or null if no job running or status unavailable</returns>
    Task<PrintJobStatusDto?> GetPrintJobStatusAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Creates multiple printers in bulk, with configurable handling of duplicates.
    /// </summary>
    /// <param name="printers">Array of printer creation DTOs</param>
    /// <param name="duplicateHandling">How to handle duplicates: "skip", "overwrite", or "error" (default: "skip")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Object containing creation results and status (successful count, failed items, etc.)</returns>
    Task<object> BulkCreatePrintersAsync(CreatePrinterFromDiscoveryDto[] printers, string duplicateHandling = "skip", CancellationToken ct = default);

    /// <summary>
    /// Imports printers from a file stream (CSV or JSON format).
    /// </summary>
    /// <param name="stream">The file stream to import from (CSV or JSON format)</param>
    /// <param name="fileName">The filename (used to detect format: .csv or .json)</param>
    /// <param name="duplicateHandling">How to handle duplicates: "skip", "overwrite", or "error" (default: "skip")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Object containing import results and status (successful count, failed items, etc.)</returns>
    Task<object> ImportFromStreamAsync(Stream stream, string fileName, string duplicateHandling = "skip", CancellationToken ct = default);

    /// <summary>
    /// Refreshes camera URLs for a printer by querying the backend API.
    /// This updates the stored camera URLs in the database.
    /// </summary>
    /// <param name="id">The printer ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated printer with refreshed camera URLs, or null if printer not found</returns>
    Task<PrinterDto?> RefreshCameraUrlsAsync(Guid id, CancellationToken ct);
}
