using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Coordinates backend-specific camera endpoint probes for printers.
/// </summary>
public sealed class PrinterCameraEndpointDetectionService(
    IPrintersService printersService,
    IEnumerable<IPrinterCameraProbe> probes,
    ILogger<PrinterCameraEndpointDetectionService> logger) : IPrinterCameraEndpointDetectionService
{
    private readonly IPrintersService _printersService = printersService ?? throw new ArgumentNullException(nameof(printersService));
    private readonly IEnumerable<IPrinterCameraProbe> _probes = probes ?? throw new ArgumentNullException(nameof(probes));
    private readonly ILogger<PrinterCameraEndpointDetectionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<PrinterCameraProbeResult?> DetectAsync(Guid printerId, CancellationToken ct = default)
    {
        if (printerId == Guid.Empty)
        {
            return null;
        }

        Printer? printer = await _printersService.FindByIdAsync(printerId, ct).ConfigureAwait(false);
        if (printer is null)
        {
            return null;
        }

        var backend = (PrinterBackend)printer.Backend;
        IPrinterCameraProbe? probe = _probes.FirstOrDefault(p => p.Backend == backend);
        string source = probe?.Source ?? GetSource(backend);
        if (probe is null)
        {
            return PrinterCameraProbeResult.NotDetected(source);
        }

        try
        {
            return await probe.DetectAsync(printer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera endpoint probe failed for printer {PrinterId} using backend {Backend}", printerId, backend);
            return PrinterCameraProbeResult.NotDetected(source);
        }
    }

    private static string GetSource(PrinterBackend backend) => backend switch
    {
        PrinterBackend.Moonraker => "klipper",
        PrinterBackend.PrusaLink => "prusalink",
        PrinterBackend.OctoPrint => "octoprint",
        PrinterBackend.SDCP => "sdcp",
        PrinterBackend.FlashForge => "flashforge",
        _ => "unknown"
    };
}
