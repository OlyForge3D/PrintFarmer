using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Hubs;

/// <summary>
/// SignalR hub for broadcasting maintenance alerts and status updates in real-time.
///
/// Group membership is authorization-scoped (issue #1966): the REST surface for maintenance
/// data is gated behind <c>maintenance:admin</c> (<see cref="Farm.Web.Api.Controllers.MaintenanceController"/>),
/// so this hub must not deliver the same data to every authenticated connection. Farm-wide
/// membership requires <c>maintenance:admin</c> (or the <c>farm_admin</c> role, which implies
/// it); per-printer membership requires <see cref="IQueueResourceAuthorizationService.CanAccessPrinterAsync"/>
/// to succeed for that printer, mirroring <see cref="Farm.Infrastructure.Services.SignalR.PrinterHub"/>.
/// </summary>
[Authorize]
public class MaintenanceHub(
    ILogger<MaintenanceHub> logger,
    IQueueResourceAuthorizationService? resourceAuthorization = null) : Hub
{
    private const string MaintenanceAdminPermission = "maintenance:admin";

    private readonly ILogger<MaintenanceHub> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Client connected to MaintenanceHub: {ConnectionId}", Context.ConnectionId);

        // Farm-wide maintenance data mirrors the REST gate on MaintenanceController
        // ([RequirePermission("maintenance", "admin")]): only a caller who already holds
        // maintenance:admin (or farm_admin, which implies it via HasPermission) is auto-joined to
        // the farm-wide group. Everyone else must explicitly subscribe to specific printers via
        // SubscribeToPrinterAsync, which is authorized per printer.
        if (PrintFarmerPermissions.HasPermission(Context.User!, MaintenanceAdminPermission))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client disconnected from MaintenanceHub: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribes the caller to maintenance alert/status/completion events for one printer, after
    /// verifying the caller can access that printer via <see cref="IQueueResourceAuthorizationService.CanAccessPrinterAsync"/>.
    /// Mirrors <see cref="Farm.Infrastructure.Services.SignalR.PrinterHub.SubscribeToPrinterAsync"/>.
    /// </summary>
    public async Task SubscribeToPrinterAsync(string printerId)
    {
        if (!Guid.TryParse(printerId, out Guid id))
        {
            throw new HubException("invalid_resource_id");
        }

        if (resourceAuthorization is null ||
            !await resourceAuthorization.CanAccessPrinterAsync(
                Context.User!,
                id,
                PrinterGroupAccessLevel.View,
                Context.ConnectionAborted))
        {
            throw new HubException("resource_forbidden");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.MaintenancePrinter(id));
    }

    /// <summary>Leaves a previously joined per-printer maintenance group.</summary>
    public Task UnsubscribeFromPrinterAsync(string printerId) =>
        Guid.TryParse(printerId, out Guid id)
            ? Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                AuthorizedHubGroups.MaintenancePrinter(id))
            : Task.CompletedTask;

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
