using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkClient : ISupportsPrinterControlCapabilities
{
    public PrinterControlCapabilities ControlCapabilities { get; } = new()
    {
        SupportsHoming = true,
        SupportsHomingXY = true,
        SupportsHomingZ = true,
        SupportsHotendTemperature = true,
        SupportsBedTemperature = true,
        SupportedAxes = Array.AsReadOnly(new[] { "x", "y", "z" }),
    };
}
