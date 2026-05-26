using Farm.Infrastructure.Contracts.Printers.Sdcp;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Sdcp;

/// <summary>
/// Detects SDCP/Elegoo camera endpoints when the printer exposes them over HTTP.
/// </summary>
public sealed class SdcpPrinterCameraProbe(ISdcpClient client) : IPrinterCameraProbe
{
    private readonly ISdcpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public PrinterBackend Backend => PrinterBackend.SDCP;

    /// <inheritdoc />
    public string Source => "sdcp";

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
