using Farm.Infrastructure;

namespace Farm.Web.Api.Controllers.Requests
{
    public class DiscoveryStreamRequest
    {
        // Optional list of backends to limit discovery to (e.g. [PrinterBackend.Moonraker, PrinterBackend.PrusaLink])
        public PrinterBackend[]? Backends { get; set; }
        
        // If true, automatically register discovered printers. Default is false - user must manually add printers.
        // Only background periodic discovery should set this to true.
        public bool AutoRegister { get; set; } = false;
    }
}
