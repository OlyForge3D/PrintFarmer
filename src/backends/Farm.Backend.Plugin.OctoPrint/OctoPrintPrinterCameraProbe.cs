using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

/// <summary>
/// Detects conventional OctoPrint webcam endpoints.
/// </summary>
public sealed class OctoPrintPrinterCameraProbe(IOctoPrintClient client) : IPrinterCameraProbe
{
    private readonly IOctoPrintClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public PrinterBackend Backend => PrinterBackend.OctoPrint;

    /// <inheritdoc />
    public string Source => "octoprint";

    /// <inheritdoc />
    public async Task<PrinterCameraProbeResult> DetectAsync(Printer printer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(printer);

        if (_client is not ISupportsCamera cameraClient)
        {
            return PrinterCameraProbeResult.NotDetected(Source);
        }

        string? streamUrl = await cameraClient.GetCameraStreamUrlAsync(printer.BackendUrl, printer.FrontendPort, printer.Credential, ct).ConfigureAwait(false);
        string? snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(printer.BackendUrl, printer.FrontendPort, printer.Credential, ct).ConfigureAwait(false);

        return PrinterCameraProbeResult.FromUrls(streamUrl, snapshotUrl, Source);
    }
}
