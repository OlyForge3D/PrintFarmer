namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Detects camera stream and snapshot endpoints for configured printers.
/// </summary>
public interface IPrinterCameraEndpointDetectionService
{
    /// <summary>
    /// Detects camera endpoints for a printer, or returns null when the printer does not exist.
    /// </summary>
    /// <param name="printerId">Printer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterCameraProbeResult?> DetectAsync(Guid printerId, CancellationToken ct = default);
}
