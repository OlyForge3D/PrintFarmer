using System.Threading;
using System.Threading.Tasks;
using Farm.Importing.Services.Adapters;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Adapters;

public class PrinterCapabilityDiscoveryAdapter : IPrinterCapabilityDiscoveryAdapter
{
    private readonly IPrinterCapabilityDiscoveryService _inner;
    public PrinterCapabilityDiscoveryAdapter(IPrinterCapabilityDiscoveryService inner) => _inner = inner;
    public Task<Farm.Infrastructure.Domain.PrinterCapabilities?> DiscoverCapabilitiesAsync(Printer printer, CancellationToken cancellationToken = default)
        => _inner.DiscoverCapabilitiesAsync(printer, cancellationToken);
}
