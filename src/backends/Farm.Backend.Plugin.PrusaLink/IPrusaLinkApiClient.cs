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
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<VersionInfo> GetVersionAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets printer information.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterInfo> GetInfoAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets the current printer status information.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StatusInfo> GetStatusAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets the current job/print status.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Job?> GetJobAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Stops the current job.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="jobId">The ID of the job to stop.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> StopJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Pauses the current job.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="jobId">The ID of the job to pause.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> PauseJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Resumes the current job.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="jobId">The ID of the job to resume.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ResumeJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Gets storage information for the printer.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="acceptLanguage">Optional Accept-Language header value.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StorageListResponse> GetStorageAsync(string baseUrl, string? apiKey = null, string? acceptLanguage = null, CancellationToken ct = default);

    /// <summary>Gets information about a specific file.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="storagePath">The storage path (e.g., "local" or "usb").</param>
    /// <param name="filePath">The path to the file within storage.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="acceptLanguage">Optional Accept-Language header value.</param>
    /// <param name="accept">Optional Accept header value.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null,
        string? acceptLanguage = null, string? accept = null, CancellationToken ct = default);

    /// <summary>Gets a list of files from the specified location (legacy endpoint).</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<System.Collections.Generic.List<FileChild>> GetFilesLegacyAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>Uploads a G-code file to the printer.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="storagePath">The storage path (e.g., "local" or "usb").</param>
    /// <param name="filePath">The destination file path within storage.</param>
    /// <param name="fileStream">The stream containing the file data to upload.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="printAfterUpload">Whether to start printing after upload.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> UploadFileAsync(string baseUrl, string storagePath, string filePath, System.IO.Stream fileStream, string? apiKey = null, bool printAfterUpload = false, bool overwrite = false, CancellationToken ct = default);

    /// <summary>Starts a print from an uploaded file.</summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="storagePath">The storage path (e.g., "local" or "usb").</param>
    /// <param name="filePath">The path to the file to print.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default);
}
