using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkClient : ISupportsPrinterControlCapabilities, ISupportsMovementDiscovery
{
    /// <inheritdoc />
    public Task<bool?> SupportsMovementAsync(
        string baseUrl,
        Farm.Infrastructure.Domain.PrinterCredential? credential,
        CancellationToken ct = default) =>
        _apiClient.SupportsMovementAsync(baseUrl, credential, ct);

    public PrinterControlCapabilities ControlCapabilities { get; } = new()
    {
        SupportsHoming = true,
        SupportsHomingXY = true,
        SupportsHomingZ = true,
        SupportsHotendTemperature = true,
        SupportsBedTemperature = true,
        SupportsTemperatureControlDuringPrint = true,
        SupportedAxes = Array.AsReadOnly(new[] { "x", "y", "z" }),
    };
}
