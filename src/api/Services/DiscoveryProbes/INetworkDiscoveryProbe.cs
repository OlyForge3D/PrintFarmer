using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.DiscoveryProbes;

public interface INetworkDiscoveryProbe
{
    string DisplayName { get; }

    /// <summary>
    /// The backend this probe detects (Moonraker/PrusaLink/SDCP/OctoPrint)
    /// </summary>
    PrinterBackend Backend { get; }

    /// <summary>
    /// Attempt to discover a printer backend at the given IP/port.
    /// </summary>
    /// <returns>A DiscoveredPrinterDto if found, otherwise null.</returns>
    Task<DiscoveredPrinterDto?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken);
}
