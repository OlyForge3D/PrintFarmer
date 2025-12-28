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
    /// <param name="result">The import progress result containing status and details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BroadcastPrinterImportProgressAsync(object result, CancellationToken cancellationToken = default);
}
