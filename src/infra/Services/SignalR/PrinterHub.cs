using Farm.Infrastructure;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Webhooks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// SignalR hub for broadcasting real-time printer status updates and discovery events.
/// This is a marker hub that servers use to send messages to connected clients.
/// Client groups are managed externally by services like PrinterDiscoveryService and MoonrakerSubscriptionService.
/// </summary>
public class PrinterHub(
    IDiscoveryProgressCache progressCache,
    ILogger<PrinterHub> logger,
    Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader statusCache,
    IWebhookService? webhookService = null) : Hub
{
    // Marker hub for broadcasting printer updates and discovery progress.

    /// <summary>
    /// Replays the cached printer statuses to a newly connected client so its UI is
    /// immediately current instead of waiting for the next backend broadcast.
    /// Also runs on automatic reconnects (each reconnect is a new connection),
    /// covering any updates missed while disconnected.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        foreach (PrinterStatusDto status in statusCache.GetAllStatuses().Values)
        {
            await Clients.Caller.SendAsync("printerupdated", status, Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Sends the cached status of a single printer back to the calling client.
    /// Wire name stays "RequestPrinterStatus" to match what the web client invokes.
    /// </summary>
    /// <param name="printerId">The printer ID to look up.</param>
    [HubMethodName("RequestPrinterStatus")]
    public async Task RequestPrinterStatusAsync(string printerId)
    {
        if (!Guid.TryParse(printerId, out Guid id))
        {
            logger.LogWarning("[PrinterHub] RequestPrinterStatus received invalid printer ID '{PrinterId}'", printerId);
            return;
        }

        PrinterStatusDto? status = statusCache.GetStatus(id);
        if (status != null)
        {
            await Clients.Caller.SendAsync("printerupdated", status, Context.ConnectionAborted);
        }
    }

    // Group management for discovery sessions

    /// <summary>
    /// Joins a client to a discovery session group to receive progress updates.
    /// </summary>
    /// <param name="sessionId">The discovery session ID to join.</param>
    public async Task JoinDiscoveryGroupAsync(string sessionId)
    {
        logger.LogInformation(
            "[PrinterHub] Client {ConnectionId} joining discovery group for session {SessionId}",
            Context.ConnectionId,
            sessionId);

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

    /// <summary>
    /// Removes a client from a discovery session group.
    /// </summary>
    /// <param name="sessionId">The discovery session ID to leave.</param>
    public async Task LeaveDiscoveryGroupAsync(string sessionId)
    {
        logger.LogInformation(
            "[PrinterHub] Client {ConnectionId} leaving discovery group for session {SessionId}",
            Context.ConnectionId,
            sessionId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
    }

    /// <summary>
    /// Called by the printer-discovery microservice to broadcast progress to clients.
    /// </summary>
    /// <param name="progress">The discovery progress data to broadcast.</param>
    public async Task BroadcastDiscoveryProgressAsync(DiscoveryProgressDto progress)
    {
        logger.LogDebug(
            "[PrinterHub] Broadcasting progress for session {SessionId}: {Percentage}%",
            progress.SessionId,
            progress.ProgressPercentage);

        // Cache the progress for late-joining clients
        progressCache.Set(progress.SessionId, progress);

        // Broadcast to all clients in the discovery session group
        await Clients.Group($"discovery-{progress.SessionId}").SendAsync("discoveryprogress", progress);
    }

    /// <summary>
    /// Called by the printer-discovery microservice to broadcast when a printer is found.
    /// </summary>
    /// <param name="found">The discovered printer data to broadcast.</param>
    public async Task BroadcastDiscoveryPrinterFoundAsync(DiscoveryPrinterFoundDto found)
    {
        logger.LogInformation(
            "[PrinterHub] Broadcasting printer found for session {SessionId}: {Name}",
            found.SessionId,
            found.Printer.Name);

        // Broadcast to all clients in the discovery session group
        await Clients.Group($"discovery-{found.SessionId}").SendAsync("discoveryprinterfound", found);

        webhookService?.Enqueue("discovery.printer_found", new
        {
            sessionId = found.SessionId,
            printerName = found.Printer.Name,
            printerIp = found.Printer.ServerUrl
        });
    }

    /// <summary>
    /// Called by the printer-discovery microservice to broadcast completion to clients.
    /// </summary>
    /// <param name="completed">The discovery completion data to broadcast.</param>
    public async Task BroadcastDiscoveryCompletedAsync(DiscoveryCompletedDto completed)
    {
        logger.LogInformation(
            "[PrinterHub] Broadcasting completion for session {SessionId}: {Found} printers found",
            completed.SessionId,
            completed.TotalPrintersFound);

        // Broadcast to all clients in the discovery session group
        await Clients.Group($"discovery-{completed.SessionId}").SendAsync("discoverycompleted", completed);

        webhookService?.Enqueue("discovery.completed", new
        {
            sessionId = completed.SessionId,
            totalPrintersFound = completed.TotalPrintersFound,
            totalPrintersExcluded = completed.TotalPrintersExcluded,
            wasCancelled = completed.WasCancelled
        });
    }
}
