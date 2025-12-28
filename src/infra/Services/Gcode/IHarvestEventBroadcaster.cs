using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Abstraction for broadcasting gcode harvest events to connected clients.
/// Implementations can use SignalR, gRPC, WebSockets, or other real-time mechanisms.
/// </summary>
public interface IHarvestEventBroadcaster
{
    /// <summary>
    /// Broadcasts a generic event to all clients in a harvest operation group.
    /// </summary>
    Task BroadcastToGroupAsync(Guid operationId, string eventName, object? data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a generic event to all connected clients.
    /// </summary>
    Task BroadcastToAllAsync(string eventName, object? data, CancellationToken cancellationToken = default);
}
