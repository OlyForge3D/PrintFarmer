using Farm.Infrastructure;
using Farm.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// SignalR hub for broadcasting real-time printer status updates and discovery events.
/// This is a marker hub that servers use to send messages to connected clients.
/// Client groups are managed externally by services like PrinterDiscoveryService and MoonrakerSubscriptionService.
/// </summary>
public class PrinterHub(IDiscoveryProgressCache progressCache, ILogger<PrinterHub> logger) : Hub
{
    // Marker hub for broadcasting printer updates and discovery progress.

    // Group management for discovery sessions
    public async Task JoinDiscoveryGroupAsync(string sessionId)
    {
        logger.LogInformation("[PrinterHub] Client {ConnectionId} joining discovery group for session {SessionId}",
            Context.ConnectionId, sessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
        // After joining, replay latest cached progress if available. There is a narrow race where the
        // controller returns the session id before the discovery service has published & cached the
        // initial progress snapshot. To mitigate, perform a brief bounded retry.
        for (int i = 0; i < 5; i++)
        {
            if (progressCache.TryGet(sessionId, out DiscoveryProgressDto? progress) && progress != null)
            {
                await Clients.Caller.SendAsync("discoveryprogress", progress);
                break;
            }
            // If cancelled/connection aborted stop early
            if (Context.ConnectionAborted.IsCancellationRequested)
            {
                break;
            }
            await Task.Delay(100, Context.ConnectionAborted);
        }
    }

    public async Task LeaveDiscoveryGroupAsync(string sessionId)
    {
        logger.LogInformation("[PrinterHub] Client {ConnectionId} leaving discovery group for session {SessionId}",
            Context.ConnectionId, sessionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
    }

    /// <summary>
    /// Called by the printer-discovery microservice to broadcast progress to clients.
    /// </summary>
    public async Task BroadcastDiscoveryProgressAsync(DiscoveryProgressDto progress)
    {
        logger.LogDebug("[PrinterHub] Broadcasting progress for session {SessionId}: {Percentage}%",
            progress.SessionId, progress.ProgressPercentage);
        // Cache the progress for late-joining clients
        progressCache.Set(progress.SessionId, progress);

        // Broadcast to all clients in the discovery session group
        await Clients.Group($"discovery-{progress.SessionId}").SendAsync("discoveryprogress", progress);
    }

    /// <summary>
    /// Called by the printer-discovery microservice to broadcast when a printer is found.
    /// </summary>
    public async Task BroadcastDiscoveryPrinterFoundAsync(DiscoveryPrinterFoundDto found)
    {
        logger.LogInformation("[PrinterHub] Broadcasting printer found for session {SessionId}: {Name}",
            found.SessionId, found.Printer.Name);
        // Broadcast to all clients in the discovery session group
        await Clients.Group($"discovery-{found.SessionId}").SendAsync("discoveryprinterfound", found);
    }

    /// <summary>
    /// Called by the printer-discovery microservice to broadcast completion to clients.
    /// </summary>
    public async Task BroadcastDiscoveryCompletedAsync(DiscoveryCompletedDto completed)
    {
        logger.LogInformation("[PrinterHub] Broadcasting completion for session {SessionId}: {Found} printers found",
            completed.SessionId, completed.TotalPrintersFound);
        // Broadcast to all clients in the discovery session group
        await Clients.Group($"discovery-{completed.SessionId}").SendAsync("discoverycompleted", completed);
    }
}
