using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Internal abstraction for PrusaLinkApiClient to enable testability.
/// This interface encapsulates HTTP communication with PrusaLink API,
/// allowing PrusaLinkClient to be tested with mocked HTTP responses.
///
/// Authentication: HTTP Digest Authentication using PrinterCredential.Username and Password.
/// PrusaLink requires digest auth for all operations.
/// </summary>
public interface IPrusaLinkApiClient
{
    // ========== PRIMARY METHODS (PrinterCredential?) ==========

    /// <summary>Gets API version information.</summary>
    Task<VersionInfo> GetVersionAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Gets printer information.</summary>
    Task<PrinterInfo> GetInfoAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Gets the current printer status information.</summary>
    Task<StatusInfo> GetStatusAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Gets the current job/print status.</summary>
    Task<Job?> GetJobAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Stops the current job (requires digest auth for write access).</summary>
    Task<bool> StopJobAsync(string baseUrl, int jobId, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Pauses the current job (requires digest auth for write access).</summary>
    Task<bool> PauseJobAsync(string baseUrl, int jobId, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Resumes the current job (requires digest auth for write access).</summary>
    Task<bool> ResumeJobAsync(string baseUrl, int jobId, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Gets storage information for the printer.</summary>
    Task<StorageListResponse> GetStorageAsync(string baseUrl, PrinterCredential? credentials = null, string? acceptLanguage = null, CancellationToken ct = default);

    /// <summary>Gets information about a specific file.</summary>
    Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credentials = null,
        string? acceptLanguage = null, string? accept = null, CancellationToken ct = default);

    /// <summary>Gets a list of files from the specified location (legacy endpoint).</summary>
    Task<System.Collections.Generic.List<FileChild>> GetFilesLegacyAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Uploads a G-code file to the printer (requires digest auth for write access).</summary>
    Task<bool> UploadFileAsync(string baseUrl, string storagePath, string filePath, System.IO.Stream fileStream, PrinterCredential? credentials = null, bool printAfterUpload = false, bool overwrite = false, CancellationToken ct = default);

    /// <summary>Starts a print from an uploaded file (requires digest auth for write access).</summary>
    Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credentials = null, CancellationToken ct = default);

    // ========== LEGACY ENDPOINTS (OctoPrint-compatible, require HTTP Digest Auth) ==========
    // These endpoints provide pause/resume, temperature control, and movement capabilities

    /// <summary>Pauses the current print job via legacy /api/job endpoint (requires digest auth).</summary>
    Task<bool> PausePrintLegacyAsync(string baseUrl, PrinterCredential? credentials, CancellationToken ct = default);

    /// <summary>Resumes the current print job via legacy /api/job endpoint (requires digest auth).</summary>
    Task<bool> ResumePrintLegacyAsync(string baseUrl, PrinterCredential? credentials, CancellationToken ct = default);

    /// <summary>Sets hotend (tool) temperature via legacy /api/printer/tool endpoint (requires digest auth).</summary>
    Task<bool> SetToolTemperatureLegacyAsync(string baseUrl, double temperature, PrinterCredential? credentials, int toolIndex = 0, CancellationToken ct = default);

    /// <summary>Sets bed temperature via legacy /api/printer/bed endpoint (requires digest auth).</summary>
    Task<bool> SetBedTemperatureLegacyAsync(string baseUrl, double temperature, PrinterCredential? credentials, CancellationToken ct = default);

    /// <summary>Jogs the print head by relative distances via legacy /api/printer/printhead endpoint (requires digest auth).</summary>
    Task<bool> JogPrintHeadLegacyAsync(string baseUrl, double? x, double? y, double? z, double? feedRate, PrinterCredential? credentials, CancellationToken ct = default);

    /// <summary>Homes specified axes via legacy /api/printer/printhead endpoint (requires digest auth).</summary>
    Task<bool> HomePrintHeadLegacyAsync(string baseUrl, bool homeX, bool homeY, bool homeZ, PrinterCredential? credentials, CancellationToken ct = default);

    /// <summary>Deletes a file from the printer's storage via PrusaLink API v1.</summary>
    Task<bool> DeleteFileAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credentials = null,
        bool force = false, CancellationToken ct = default);

    /// <summary>Gets a list of print history jobs from OctoPrint-compatible history endpoint.</summary>
    Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, DateTime? since = null, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Gets details for a specific history job from OctoPrint-compatible history endpoint.</summary>
    Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Gets aggregated totals computed from available history jobs.</summary>
    Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default);

    /// <summary>Deletes a history job if endpoint supports deletion.</summary>
    Task<bool> DeleteHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credentials = null, CancellationToken ct = default);
}
