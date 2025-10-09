using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.DiscoveryProbes;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.DiscoveryProbes;

[DiscoveryProbe(Name)]
public class MoonrakerDiscoveryProbe : BaseDiscoveryProbe
{
    private const string Name = "Moonraker";
    public override string DisplayName => Name;
    protected override int[] Ports => new[] { 7125, 80 };
    protected override string EndpointPath => "/printer/info";
    protected override PrinterBackend Backend => PrinterBackend.Moonraker;
    protected override string PrinterName => "Moonraker Printer";
    // Optionally override IsValidResponse if needed
}
