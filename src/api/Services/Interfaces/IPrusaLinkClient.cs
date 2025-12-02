using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Interface for PrusaLink client providing communication with Prusa printers via PrusaLink API.
/// Supports printer status monitoring, job control, file management, and basic printer operations.
/// </summary>
public interface IPrusaLinkClient
{
    /// <summary>
    /// Gets comprehensive status information combining printer state, job progress, and camera information.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server (e.g., http://printer-ip)</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing detailed printer status including state, progress, and camera URLs</returns>
    Task<PrusaCompositeStatus> GetCompositeStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default);
    // Analyzer-friendly overload (non-breaking): allow Uri input too
    Task<PrusaCompositeStatus> GetCompositeStatusAsync(Uri baseUrl, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Gets the basic status information from a PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing printer status information including online status and state</returns>
    Task<PrusaStatus> GetStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default);
    Task<PrusaStatus> GetStatusAsync(Uri baseUrl, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Gets the current print job information from a PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing current job information, or null if no job is active</returns>
    Task<PrusaJob?> GetJobAsync(string baseUrl, string? apiKey, CancellationToken ct = default);
    Task<PrusaJob?> GetJobAsync(Uri baseUrl, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Gets the URL for accessing the camera snapshot image.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server (host only)</param>
    /// <param name="frontendPort">Optional frontend port for camera access (defaults to 80 for HTTP, 443 for HTTPS)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the camera snapshot URL, or null if configuration fails</returns>
    Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, CancellationToken ct = default);
    Task<string?> GetCameraSnapshotUrlAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the URL for accessing the camera stream (typically MJPEG).
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server (host only)</param>
    /// <param name="frontendPort">Optional frontend port for camera access (defaults to 80 for HTTP, 443 for HTTPS)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the camera stream URL, or null if configuration fails</returns>
    Task<string?> GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, CancellationToken ct = default);
    Task<string?> GetCameraStreamUrlAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default);

    /// <summary>
    /// Uploads a G-code file to the PrusaLink printer's storage.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="fileName">The name to save the file as</param>
    /// <param name="fileContent">Stream containing the G-code file content</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the upload was successful</returns>
    Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default);
    Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default);

    /// <summary>
    /// Starts printing a G-code file by name on the PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="fileName">The name of the G-code file to print</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the print start command was successfully sent</returns>
    Task<bool> StartPrintAsync(string baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default);
    Task<bool> StartPrintAsync(Uri baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default);

    /// <summary>
    /// Gets a list of G-code file names available on the PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing an array of G-code file names</returns>
    Task<string[]> GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
    Task<string[]> GetFileListAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>
    /// Gets a list of file details including names and paths for metadata retrieval.
    /// Used internally for thumbnail extraction.
    /// </summary>
    Task<List<(string Name, string Path)>> GetFileDetailsListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
    Task<List<(string Name, string Path)>> GetFileDetailsListAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed file information including metadata and thumbnail URLs.
    /// Used for retrieving thumbnail information for display.
    /// </summary>
    Task<FileInfoBase> GetFileDetailsAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default);
    Task<FileInfoBase> GetFileDetailsAsync(Uri baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed printer information from a PrusaLink printer (name, firmware, capabilities, etc.)
    /// </summary>
    Task<PrinterInformation?> GetPrinterInformationAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
    Task<PrinterInformation?> GetPrinterInformationAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default);

    /// <summary>
    /// Creates a PrinterDto from a database Printer entity and its composite status.
    /// Encapsulates backend-specific DTO creation logic within the PrusaLink client.
    /// </summary>
    /// <param name="printer">The printer entity from the database (includes FrontendPort)</param>
    /// <param name="status">The composite status retrieved from the printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the fully constructed PrinterDto with all backend-specific details</returns>
    Task<PrinterDto> CreatePrinterDtoAsync(
        Printer printer,
        PrusaCompositeStatus status,
        CancellationToken ct = default);
}
