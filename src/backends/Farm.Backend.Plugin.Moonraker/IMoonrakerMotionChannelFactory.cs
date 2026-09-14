using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>Creates the Moonraker plugin's pinned correlated command transport.</summary>
public interface IMoonrakerMotionChannelFactory
{
    Task<IPrinterMotionChannel> ConnectAsync(Printer printer, CancellationToken ct);
}
