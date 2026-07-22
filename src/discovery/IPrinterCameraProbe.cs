using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Detects camera endpoint URLs for one printer backend implementation.
/// </summary>
public interface IPrinterCameraProbe
{
    /// <summary>
    /// Gets the printer backend handled by this probe.
    /// </summary>
    PrinterBackend Backend { get; }

    /// <summary>
    /// Gets the stable lowercase source identifier returned to API clients.
    /// </summary>
    string Source { get; }

    /// <summary>
    /// Detects configured camera endpoint URLs for a printer.
    /// </summary>
    /// <param name="printer">Printer to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterCameraProbeResult> DetectAsync(Printer printer, CancellationToken ct = default);
}

/// <summary>
/// Result from a backend-specific printer camera probe.
/// </summary>
public sealed record PrinterCameraProbeResult(string? StreamUrl, string? SnapshotUrl, bool Detected, string Source)
{
    /// <summary>
    /// Creates a successful detection result if at least one URL is present.
    /// </summary>
    public static PrinterCameraProbeResult FromUrls(string? streamUrl, string? snapshotUrl, string source)
        => new(streamUrl, snapshotUrl, !string.IsNullOrWhiteSpace(streamUrl) || !string.IsNullOrWhiteSpace(snapshotUrl), source);

    /// <summary>
    /// Creates an unsupported or failed probe result.
    /// </summary>
    public static PrinterCameraProbeResult NotDetected(string source)
        => new(null, null, false, source);
}
