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

/// <summary>
/// Extension methods for single file harvest progress broadcasting
/// </summary>
public static class HarvestEventBroadcasterExtensions
{
    /// <summary>
    /// Broadcasts single file harvest start event to all clients
    /// </summary>
    public static Task BroadcastSingleFileHarvestStartAsync(
        this IHarvestEventBroadcaster broadcaster,
        string fileName,
        CancellationToken ct = default)
    {
        return broadcaster.BroadcastToAllAsync("singlefileharveststart", new
        {
            fileName,
            timestamp = DateTime.UtcNow
        }, ct);
    }

    /// <summary>
    /// Broadcasts single file harvest progress update to all clients
    /// </summary>
    public static Task BroadcastSingleFileHarvestProgressAsync(
        this IHarvestEventBroadcaster broadcaster,
        string fileName,
        int percentComplete,
        string message,
        CancellationToken ct = default)
    {
        return broadcaster.BroadcastToAllAsync("singlefileharvestprogress", new
        {
            fileName,
            percentComplete,
            message,
            timestamp = DateTime.UtcNow
        }, ct);
    }

    /// <summary>
    /// Broadcasts single file harvest completion event to all clients
    /// </summary>
    public static Task BroadcastSingleFileHarvestCompleteAsync(
        this IHarvestEventBroadcaster broadcaster,
        string fileName,
        bool success,
        string message,
        CancellationToken ct = default)
    {
        return broadcaster.BroadcastToAllAsync("singlefileharvestcomplete", new
        {
            fileName,
            success,
            message,
            timestamp = DateTime.UtcNow
        }, ct);
    }
}
