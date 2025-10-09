using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.DiscoveryProbes;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.DiscoveryProbes;

[DiscoveryProbe(Name)]
public class PrusaLinkDiscoveryProbe : BaseDiscoveryProbe
{
    private const string Name = "PrusaLink";
    public override string DisplayName => Name;
    protected override int[] Ports => new[] { 80, 8080 };
    protected override string EndpointPath => "/api/v1/info";
    protected override PrinterBackend Backend => PrinterBackend.PrusaLink;
    protected override string PrinterName => "PrusaLink Printer";
    // Optionally override IsValidResponse if needed
}
