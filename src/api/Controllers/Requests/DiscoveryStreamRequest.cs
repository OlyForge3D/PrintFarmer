using Farm.Web.Shared;

namespace Farm.Web.Api.Controllers.Requests
{
    public class DiscoveryStreamRequest
    {
        // Optional list of backends to limit discovery to (e.g. [PrinterBackend.Moonraker, PrinterBackend.PrusaLink])
        public PrinterBackend[]? Backends { get; set; }
    }
}
