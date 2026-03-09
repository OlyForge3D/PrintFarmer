using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Printers;

/// <summary>
/// SignalR implementation of printer status broadcaster.
/// Broadcasts events to all connected clients using SignalR hubs.
/// </summary>
public class SignalRPrinterStatusBroadcaster(
    IHubContext<PrinterHub> hubContext,
    IPrinterStatusCacheWriter statusCacheWriter) : Farm.Infrastructure.Services.Printers.IPrinterStatusBroadcaster
{
    private readonly IHubContext<PrinterHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter ?? throw new ArgumentNullException(nameof(statusCacheWriter));

    /// <inheritdoc />
    public async Task BroadcastPrinterImportProgressAsync(object result, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.All.SendAsync("printerimportprogress", result, cancellationToken);
    }

    /// <inheritdoc />
    public async Task BroadcastSpoolChangeAsync(Guid printerId, PrinterSpoolInfoDto? spoolInfo, CancellationToken cancellationToken = default)
    {
        // Atomically update only the SpoolInfo field in the cache
        PrinterStatusDto updated = _statusCacheWriter.UpdateSpoolInfo(printerId, spoolInfo);

        // Push the updated status to all connected clients
        await _hubContext.Clients.All.SendAsync("printerupdated", updated, cancellationToken);
    }
}
