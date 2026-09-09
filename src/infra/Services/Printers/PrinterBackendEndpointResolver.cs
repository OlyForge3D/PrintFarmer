namespace Farm.Infrastructure.Services.Printers;

/// <summary>Resolves the exact backend endpoint used by shared printer dispatch routes.</summary>
public static class PrinterBackendEndpointResolver
{
    /// <summary>Builds the Moonraker dispatch endpoint used by printer actuation.</summary>
    /// <param name="serverUrl">Configured printer server URL.</param>
    /// <param name="frontendPort">Configured frontend proxy port.</param>
    /// <returns>The endpoint consumed by Moonraker dispatch methods.</returns>
    public static string ResolveMoonrakerDispatchUrl(
        string serverUrl,
        int? frontendPort)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return serverUrl;
        }

        try
        {
            Uri baseUri = new(serverUrl);
            int port =
                frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);
            return new UriBuilder(baseUri) { Port = port }
                .Uri.ToString()
                .TrimEnd('/');
        }
        catch (UriFormatException)
        {
            return serverUrl;
        }
    }

    /// <summary>Resolves the endpoint used to dispatch to a printer backend.</summary>
    /// <param name="printer">Printer configuration.</param>
    /// <returns>Exact dispatch endpoint for that backend.</returns>
    public static string ResolveDispatchUrl(Domain.Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        return (Domain.PrinterBackend)printer.Backend ==
            Domain.PrinterBackend.Moonraker
                ? ResolveMoonrakerDispatchUrl(
                    printer.ServerUrl,
                    printer.FrontendPort)
                : printer.BackendUrl;
    }
}
