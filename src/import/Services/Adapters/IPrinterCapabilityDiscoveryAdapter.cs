using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Importing.Services.Adapters;

public interface IPrinterCapabilityDiscoveryAdapter
{
    Task<Farm.Infrastructure.Domain.PrinterCapabilities?> DiscoverCapabilitiesAsync(Printer printer, CancellationToken cancellationToken = default);
}
