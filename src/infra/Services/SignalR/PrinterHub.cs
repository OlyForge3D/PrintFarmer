using Farm.Infrastructure;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// SignalR hub for broadcasting real-time printer status updates and discovery events.
/// This is a marker hub that servers use to send messages to connected clients.
/// Client groups are managed externally by services like PrinterDiscoveryService and MoonrakerSubscriptionService.
/// </summary>
[Authorize]
public class PrinterHub(
    IDiscoveryProgressCache progressCache,
    ILogger<PrinterHub> logger,
    Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader statusCache,
    IDiscoverySessionRegistry discoverySessions) : Hub
{
    // Marker hub for broadcasting printer updates and discovery progress.

    /// <summary>
    /// SignalR group reserved for administrator-only task broadcasts. Only
    /// connections whose authenticated principal holds the <c>farm_admin</c> role
    /// join this group.
    /// </summary>
    public const string AdminTaskGroup = "farm_admin";

    /// <summary>
    /// Adds the connection to its authorized SignalR groups, then replays the cached
    /// printer statuses to the newly connected client so its UI is immediately current
    /// instead of waiting for the next backend broadcast. Also runs on automatic
    /// reconnects (each reconnect is a new connection), covering any updates missed
    /// while disconnected.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm);

        if (PrintFarmerPermissions.TryGetUserId(Context.User!, out Guid userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.User(userId));
        }

        if (PrintFarmerPermissions.IsFarmAdmin(Context.User!))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Administrators);
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                AdminTaskGroup,
                Context.ConnectionAborted);
        }

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
        if (!PrintFarmerPermissions.TryGetUserId(Context.User!, out Guid userId))
        {
            throw new HubException("authentication_required");
        }

        bool isOwner = discoverySessions.IsSessionOwner(sessionId, userId);
        bool isFarmAdmin = PrintFarmerPermissions.IsFarmAdmin(Context.User!);
        if (!discoverySessions.SessionExists(sessionId) || (!isOwner && !isFarmAdmin))
        {
            logger.LogWarning(
                "Denied discovery group subscription by user {UserId} for session {SessionId}",
                userId,
                sessionId);
            throw new HubException("resource_forbidden");
        }

        if (!isOwner)
        {
            logger.LogInformation(
                "Audited farm-admin discovery session bypass by user {UserId} for session {SessionId}",
                userId,
                sessionId);
        }

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
}
