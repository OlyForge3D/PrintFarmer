namespace Farm.Infrastructure.Services.Printers;

/// <summary>Resolves the exact backend endpoint used by shared printer dispatch routes.</summary>
public static class PrinterBackendEndpointResolver
{
    /// <summary>Resolves the endpoint used to dispatch to a printer backend.</summary>
    /// <param name="printer">Printer configuration.</param>
    /// <returns>Exact dispatch endpoint for that backend.</returns>
    public static string ResolveDispatchUrl(Domain.Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        // A web UI need not proxy API routes. Discovery and dispatch must target
        // the same configured backend as the physical-control motion channel.
        return printer.BackendUrl;
    }
}
