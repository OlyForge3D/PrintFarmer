using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Applies a backend-neutral sensor change to the status cache and, when the sensor content
    /// changed, pushes the full cached status as a <c>printerupdated</c> event to the printer's
    /// authorized subscription group. Sensor-only and availability-only changes are delivered even
    /// when legacy status fields are unchanged.
    /// </summary>
    /// <param name="printerId">The printer whose sensors changed.</param>
    /// <param name="sensors">A full snapshot when <paramref name="replaceAll"/> is true; otherwise a partial update.</param>
    /// <param name="replaceAll">Whether <paramref name="sensors"/> replaces the whole sensor list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when an event was broadcast.</returns>
    Task<bool> BroadcastSensorChangeAsync(
        Guid printerId,
        IEnumerable<PrinterSensorDto> sensors,
        bool replaceAll,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
