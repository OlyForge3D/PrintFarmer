using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request to test connectivity to a printer backend before adding the printer.
/// </summary>
/// <param name="ServerUrl">The server URL of the printer (e.g., http://192.168.1.100)</param>
/// <param name="Backend">The backend type (Moonraker, PrusaLink, OctoPrint, SDCP)</param>
/// <param name="ApiKey">API key for authentication (required for OctoPrint)</param>
/// <param name="Username">Username for HTTP Digest authentication (required for PrusaLink, defaults to "maker")</param>
/// <param name="Password">Password for HTTP Digest authentication (required for PrusaLink)</param>
/// <param name="BackendPort">Backend port for API communication (default 7125 for Moonraker)</param>
public record TestConnectionRequest(
    string ServerUrl,
    PrinterBackend Backend,
    string? ApiKey = null,
    string? Username = null,
    string? Password = null,
    int? BackendPort = null);
