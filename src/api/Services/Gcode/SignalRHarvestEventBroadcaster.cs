using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Gcode;

/// <summary>
/// SignalR implementation of harvest event broadcaster.
/// Broadcasts harvest events to all connected clients using SignalR hubs.
/// </summary>
public class SignalRHarvestEventBroadcaster : IHarvestEventBroadcaster
{
    private readonly IHubContext<HarvestHub> _hubContext;

    public SignalRHarvestEventBroadcaster(IHubContext<HarvestHub> hubContext)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    }

    /// <summary>
    /// Broadcasts a generic event to all clients in a harvest operation group via SignalR.
    /// </summary>
    public async Task BroadcastToGroupAsync(Guid operationId, string eventName, object? data, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.Group($"harvest-{operationId}")
            .SendAsync(eventName, data, cancellationToken);
    }

    /// <summary>
    /// Broadcasts a generic event to all connected clients via SignalR.
    /// </summary>
    public async Task BroadcastToAllAsync(string eventName, object? data, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.All.SendAsync(eventName, data, cancellationToken);
    }
}
