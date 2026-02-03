using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Internal abstraction for PrusaLinkApiClient to enable testability.
/// This interface encapsulates HTTP communication with PrusaLink API,
/// allowing PrusaLinkClient to be tested with mocked HTTP responses.
///
/// Authentication modes:
/// - API key only (X-Api-Key header): Read access to most endpoints
/// - HTTP Digest Auth: Full access including privileged operations
/// - Both: API key for read, digest for write operations
///
/// All methods have overloads accepting either:
/// - PrusaLinkCredentials? credentials - for full auth control
/// - string? apiKey - for backward compatibility (API key only)
/// </summary>
public interface IPrusaLinkApiClient
{
    // ========== PRIMARY METHODS (PrusaLinkCredentials) ==========

    /// <summary>Gets API version information.</summary>
    Task<VersionInfo> GetVersionAsync(string baseUrl, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Gets printer information.</summary>
    Task<PrinterInfo> GetInfoAsync(string baseUrl, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Gets the current printer status information.</summary>
    Task<StatusInfo> GetStatusAsync(string baseUrl, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Gets the current job/print status.</summary>
    Task<Job?> GetJobAsync(string baseUrl, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Stops the current job (requires digest auth for write access).</summary>
    Task<bool> StopJobAsync(string baseUrl, int jobId, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Pauses the current job (requires digest auth for write access).</summary>
    Task<bool> PauseJobAsync(string baseUrl, int jobId, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Resumes the current job (requires digest auth for write access).</summary>
    Task<bool> ResumeJobAsync(string baseUrl, int jobId, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Gets storage information for the printer.</summary>
    Task<StorageListResponse> GetStorageAsync(string baseUrl, PrusaLinkCredentials? credentials = null, string? acceptLanguage = null, CancellationToken ct = default);

    /// <summary>Gets information about a specific file.</summary>
    Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, PrusaLinkCredentials? credentials = null,
        string? acceptLanguage = null, string? accept = null, CancellationToken ct = default);

    /// <summary>Gets a list of files from the specified location (legacy endpoint).</summary>
    Task<System.Collections.Generic.List<FileChild>> GetFilesLegacyAsync(string baseUrl, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    /// <summary>Uploads a G-code file to the printer (requires digest auth for write access).</summary>
    Task<bool> UploadFileAsync(string baseUrl, string storagePath, string filePath, System.IO.Stream fileStream, PrusaLinkCredentials? credentials = null, bool printAfterUpload = false, bool overwrite = false, CancellationToken ct = default);

    /// <summary>Starts a print from an uploaded file (requires digest auth for write access).</summary>
    Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, PrusaLinkCredentials? credentials = null, CancellationToken ct = default);

    // ========== BACKWARD COMPATIBLE OVERLOADS (string apiKey) ==========
    // These methods accept string? apiKey for backward compatibility with existing code.
    // They delegate to the primary methods after converting apiKey to PrusaLinkCredentials.

    /// <summary>Gets API version information (backward compatible).</summary>
    Task<VersionInfo> GetVersionAsync(string baseUrl, string? apiKey, CancellationToken ct);

    /// <summary>Gets printer information (backward compatible).</summary>
    Task<PrinterInfo> GetInfoAsync(string baseUrl, string? apiKey, CancellationToken ct);

    /// <summary>Gets the current printer status information (backward compatible).</summary>
    Task<StatusInfo> GetStatusAsync(string baseUrl, string? apiKey, CancellationToken ct);

    /// <summary>Gets the current job/print status (backward compatible).</summary>
    Task<Job?> GetJobAsync(string baseUrl, string? apiKey, CancellationToken ct);

    /// <summary>Stops the current job (backward compatible).</summary>
    Task<bool> StopJobAsync(string baseUrl, int jobId, string? apiKey, CancellationToken ct);

    /// <summary>Pauses the current job (backward compatible).</summary>
    Task<bool> PauseJobAsync(string baseUrl, int jobId, string? apiKey, CancellationToken ct);

    /// <summary>Resumes the current job (backward compatible).</summary>
    Task<bool> ResumeJobAsync(string baseUrl, int jobId, string? apiKey, CancellationToken ct);

    /// <summary>Gets storage information (backward compatible).</summary>
    Task<StorageListResponse> GetStorageAsync(string baseUrl, string? apiKey, CancellationToken ct);

    /// <summary>Gets information about a specific file (backward compatible).</summary>
    Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, string? apiKey, CancellationToken ct);

    /// <summary>Gets a list of files from the specified location (backward compatible).</summary>
    Task<System.Collections.Generic.List<FileChild>> GetFilesLegacyAsync(string baseUrl, string? apiKey, CancellationToken ct);

    /// <summary>Uploads a G-code file to the printer (backward compatible).</summary>
    Task<bool> UploadFileAsync(string baseUrl, string storagePath, string filePath, System.IO.Stream fileStream, string? apiKey, bool printAfterUpload, bool overwrite, CancellationToken ct);

    /// <summary>Starts a print from an uploaded file (backward compatible).</summary>
    Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, string? apiKey, CancellationToken ct);
}
