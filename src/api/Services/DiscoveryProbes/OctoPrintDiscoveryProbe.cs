using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.DiscoveryProbes;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.DiscoveryProbes;

[DiscoveryProbe(Name)]
public class OctoPrintDiscoveryProbe : BaseDiscoveryProbe
{
    private const string Name = "OctoPrint";
    public override string DisplayName => Name;
    protected override int[] Ports => new[] { 80, 5000 };
    protected override string EndpointPath => "/api/version";
    protected override PrinterBackend Backend => PrinterBackend.OctoPrint;
    protected override string PrinterName => "OctoPrint Printer";
    // Optionally override IsValidResponse if needed
}
