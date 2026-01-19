using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Printers;

/// <summary>
/// SignalR implementation of printer status broadcaster.
/// Broadcasts events to all connected clients using SignalR hubs.
/// </summary>
public class SignalRPrinterStatusBroadcaster(IHubContext<PrinterHub> hubContext) : Farm.Infrastructure.Services.Printers.IPrinterStatusBroadcaster
{
    private readonly IHubContext<PrinterHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    /// <summary>
    /// Broadcasts printer import progress to all connected clients via SignalR.
    /// </summary>
    /// <param name="result">The import progress result to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    public async Task BroadcastPrinterImportProgressAsync(object result, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.All.SendAsync("printerimportprogress", result, cancellationToken);
    }
}
