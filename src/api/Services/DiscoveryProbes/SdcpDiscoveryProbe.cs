using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;
using Farm.Web.Api.Services.DiscoveryProbes;

namespace Farm.Web.Api.Services.DiscoveryProbes;

// ...existing code...
// ...existing code...
[DiscoveryProbe(Name)]
public class SdcpDiscoveryProbe : BaseDiscoveryProbe
{
    private const string Name = "SDCP";
    public override string DisplayName => Name;
    protected override int[] Ports => new[] { 80, 7125 };
    protected override string EndpointPath => "/sdcp/info";
    protected override PrinterBackend Backend => PrinterBackend.SDCP;
    protected override string PrinterName => "SDCP Printer";
    // Optionally override IsValidResponse if needed
}
