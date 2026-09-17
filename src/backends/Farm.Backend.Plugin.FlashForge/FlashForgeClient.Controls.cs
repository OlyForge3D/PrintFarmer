using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.FlashForge;

public sealed partial class FlashForgeClient : ISupportsPrinterControlCapabilities
{
    public PrinterControlCapabilities ControlCapabilities { get; } = new()
    {
        SupportsHotendTemperature = true,
        SupportsBedTemperature = true,
    };
}
