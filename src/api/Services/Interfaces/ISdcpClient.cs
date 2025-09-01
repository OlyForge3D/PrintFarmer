namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Interface for SDCP (Smart Device Control Protocol) client providing communication with SDCP-compatible printers.
/// Supports printer status monitoring, job control, camera operations, and file management for Elegoo and other SDCP printers.
/// Implements IDisposable to properly cleanup WebSocket connections.
/// </summary>
public interface ISdcpClient : IDisposable
{
    /// <summary>
    /// Gets the basic status information from an SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer (e.g., ws://printer-ip)</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing printer status information including online status and state</returns>
    Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets the current print job information from an SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing current job information</returns>
    Task<PrinterJob> GetJobAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets comprehensive status information combining printer state, job progress, position, and temperature data.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing detailed printer status including temperatures, position, and job progress</returns>
    Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Starts printing a G-code file by name on the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="filename">The name of the G-code file to print</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the print start command was successfully sent</returns>
    Task<bool> StartPrintAsync(string baseUrl, string filename, CancellationToken ct = default);

    /// <summary>
    /// Pauses the current print job on the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the pause command was successfully sent</returns>
    Task<bool> PausePrintAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Cancels the current print job on the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the cancel command was successfully sent</returns>
    Task<bool> CancelPrintAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Resumes a paused print job on the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the resume command was successfully sent</returns>
    Task<bool> ResumePrintAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets the camera stream URL from the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the camera stream URL, or null if no camera is available</returns>
    Task<string?> GetCameraUrlAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets the camera snapshot URL from the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing the camera snapshot URL, or null if no camera is available</returns>
    Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Enables the camera on the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the camera enable command was successfully sent</returns>
    Task<bool> EnableCameraAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Disables the camera on the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the camera disable command was successfully sent</returns>
    Task<bool> DisableCameraAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets a list of G-code file names available on the SDCP printer.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task containing an array of G-code file names</returns>
    Task<string[]> GetFileListAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Uploads a G-code file to the SDCP printer's storage.
    /// </summary>
    /// <param name="baseUrl">The WebSocket URL of the SDCP printer</param>
    /// <param name="fileName">The name to save the file as</param>
    /// <param name="fileContent">Stream containing the G-code file content</param>
    /// <param name="ct">Cancellation token to cancel the operation</param>
    /// <returns>A task indicating whether the upload was successful</returns>
    Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, CancellationToken ct = default);
}
