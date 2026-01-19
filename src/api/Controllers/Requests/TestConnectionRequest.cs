using Farm.Infrastructure;

namespace Farm.Web.Api.Controllers.Requests;
/// <summary>
/// Request to test connectivity to a printer backend before adding the printer.
/// </summary>
/// <param name="ServerUrl">The server URL of the printer (e.g., http://192.168.1.100)</param>
/// <param name="Backend">The backend type (Moonraker, PrusaLink, OctoPrint, SDCP)</param>
/// <param name="ApiKey">API key for authentication (required for PrusaLink and OctoPrint)</param>
/// <param name="BackendPort">Backend port for API communication (default 7125 for Moonraker)</param>
public record TestConnectionRequest(
    string ServerUrl,
    PrinterBackend Backend,
    string? ApiKey = null,
    int? BackendPort = null);
