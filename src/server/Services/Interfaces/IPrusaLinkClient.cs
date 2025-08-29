using Farm.Web.Server.Services;

namespace Farm.Web.Server.Services.Interfaces;

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
    
    /// <summary>
    /// Gets the basic status information from a PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing printer status information including online status and state</returns>
    Task<PrusaStatus> GetStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the current print job information from a PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing current job information, or null if no job is active</returns>
    Task<PrusaJob?> GetJobAsync(string baseUrl, string? apiKey, CancellationToken ct = default);
    
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
    
    /// <summary>
    /// Starts printing a G-code file by name on the PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="fileName">The name of the G-code file to print</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the print start command was successfully sent</returns>
    Task<bool> StartPrintAsync(string baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default);
    
    /// <summary>
    /// Gets a list of G-code file names available on the PrusaLink printer.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink server</param>
    /// <param name="apiKey">API key for authentication with PrusaLink</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing an array of G-code file names</returns>
    Task<string[]> GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
}
