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
    IDiscoverySessionRegistry discoverySessions,
    ILogger<PrinterHub> logger) : Hub
{
    // Marker hub for broadcasting printer updates and discovery progress.

    /// <inheritdoc />
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
        }

        await base.OnConnectedAsync();
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
