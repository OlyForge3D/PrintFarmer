using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Internal abstraction for PrusaLinkApiClient to enable testability.
/// This interface encapsulates HTTP communication with PrusaLink API,
/// allowing PrusaLinkClient to be tested with mocked HTTP responses.
/// </summary>
public interface IPrusaLinkApiClient
{
    /// <summary>Gets API version information.</summary>
    Task<VersionInfo> GetVersionAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets printer information.</summary>
    Task<PrinterInfo> GetInfoAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets the current printer status information.</summary>
    Task<StatusInfo> GetStatusAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets the current job/print status.</summary>
    Task<Job?> GetJobAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Stops the current job.</summary>
    Task<bool> StopJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Pauses the current job.</summary>
    Task<bool> PauseJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Resumes the current job.</summary>
    Task<bool> ResumeJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets storage information for the printer.</summary>
    Task<StorageListResponse> GetStorageAsync(string baseUrl, string? apiKey = null, string? acceptLanguage = null, CancellationToken ct = default);

    /// <summary>Gets information about a specific file.</summary>
    Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null,
        string? acceptLanguage = null, string? accept = null, CancellationToken ct = default);

    /// <summary>Gets a list of files from the specified location (legacy endpoint).</summary>
    Task<System.Collections.Generic.List<FileChild>> GetFilesLegacyAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Uploads a G-code file to the printer.</summary>
    Task<bool> UploadFileAsync(string baseUrl, string storagePath, string filePath, System.IO.Stream fileStream, string? apiKey = null, bool printAfterUpload = false, bool overwrite = false, CancellationToken ct = default);

    /// <summary>Starts a print from an uploaded file.</summary>
    Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default);
}
