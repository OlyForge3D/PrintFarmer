using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Hubs;

/// <summary>
/// SignalR hub for broadcasting maintenance alerts and status updates in real-time.
/// </summary>
public class MaintenanceHub(ILogger<MaintenanceHub> logger) : Hub
{
    private readonly ILogger<MaintenanceHub> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Client connected to MaintenanceHub: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client disconnected from MaintenanceHub: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client can call this to request current alert state.
    /// </summary>
    public async Task RequestAlertsUpdateAsync()
    {
        _logger.LogDebug("Client {ConnectionId} requested alerts update", Context.ConnectionId);

        // The client should call GET /api/maintenance/alerts after receiving this acknowledgment
        await Clients.Caller.SendAsync("AlertsUpdateRequested");
    }
}

/// <summary>
/// Events broadcast by the MaintenanceHub (called from MaintenanceAlertEngine).
/// All event names are lowercase for consistency with other SignalR hubs.
/// </summary>
public static class MaintenanceHubEvents
{
    /// <summary>
    /// Event name for when a new maintenance alert is created.
    /// Payload: { id: Guid, printerId: Guid, title: string, message: string, severity: int, createdAt: DateTime }
    /// </summary>
    public const string AlertCreated = "alertcreated";

    /// <summary>
    /// Event name for when an alert status changes (acknowledged/resolved/dismissed).
    /// Payload: { id: Guid, printerId: Guid, status: string, acknowledgedAt: DateTime?, acknowledgedBy: string?, resolvedAt: DateTime?, resolvedBy: string?, dismissedAt: DateTime?, dismissedBy: string? }
    /// </summary>
    public const string AlertStatusChanged = "alertstatuschanged";

    /// <summary>
    /// Event name for when a maintenance log is created (Phase 4).
    /// Payload: { id: Guid, printerId: Guid, scheduleId: Guid, completedAt: DateTime }
    /// </summary>
    public const string MaintenanceCompleted = "maintenancecompleted";
}
