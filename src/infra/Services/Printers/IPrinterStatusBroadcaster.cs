using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Abstraction for broadcasting printer status updates and events to connected clients.
/// Implementations can use SignalR, gRPC, WebSockets, or other real-time mechanisms.
/// </summary>
public interface IPrinterStatusBroadcaster
{
    /// <summary>
    /// Broadcasts a printer import progress update to all connected clients.
    /// </summary>
    Task BroadcastPrinterImportProgressAsync(object result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a spool assignment change for a printer to all connected clients.
    /// Updates the status cache and pushes a printerupdated SignalR event.
    /// </summary>
    Task BroadcastSpoolChangeAsync(Guid printerId, PrinterSpoolInfoDto? spoolInfo, CancellationToken cancellationToken = default);
}
