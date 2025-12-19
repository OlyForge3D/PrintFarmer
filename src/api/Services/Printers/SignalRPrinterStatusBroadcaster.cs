using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.SignalR.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Printers;

/// <summary>
/// SignalR implementation of printer status broadcaster.
/// Broadcasts events to all connected clients using SignalR hubs.
/// </summary>
public class SignalRPrinterStatusBroadcaster : Farm.Infrastructure.Services.Printers.IPrinterStatusBroadcaster
{
    private readonly IHubContext<PrinterHub> _hubContext;

    public SignalRPrinterStatusBroadcaster(IHubContext<PrinterHub> hubContext)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    }

    /// <summary>
    /// Broadcasts printer import progress to all connected clients via SignalR.
    /// </summary>
    public async Task BroadcastPrinterImportProgressAsync(object result, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.All.SendAsync("printerImportProgress", result, cancellationToken);
    }
}
