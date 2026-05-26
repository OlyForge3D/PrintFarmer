using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>
/// Detects configured camera endpoints from Moonraker's webcam API.
/// </summary>
public sealed class MoonrakerPrinterCameraProbe(IMoonrakerClient client) : IPrinterCameraProbe
{
    private readonly IMoonrakerClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public PrinterBackend Backend => PrinterBackend.Moonraker;

    /// <inheritdoc />
    public string Source => "klipper";

    /// <inheritdoc />
    public async Task<PrinterCameraProbeResult> DetectAsync(Printer printer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(printer);

        if (_client is not ISupportsConfiguredCameraDetection detectionClient)
        {
            return PrinterCameraProbeResult.NotDetected(Source);
        }

        (string? streamUrl, string? snapshotUrl) = await detectionClient
            .DetectConfiguredCameraUrlsAsync(printer.BackendUrl, printer.FrontendPort, printer.Credential, ct)
            .ConfigureAwait(false);

        return PrinterCameraProbeResult.FromUrls(streamUrl, snapshotUrl, Source);
    }
}
