using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Maintenance;

/// <summary>
/// Publishes maintenance-completion SignalR and webhook events after the resolution transaction
/// commits. Each transport is isolated so notification failures never change the HTTP result.
/// </summary>
public sealed class MaintenanceResolutionNotifier(
    IHubContext<MaintenanceHub> maintenanceHub,
    IWebhookService webhookService,
    ILogger<MaintenanceResolutionNotifier> logger) : IMaintenanceResolutionNotifier
{
    /// <inheritdoc />
    public async Task NotifyCreatedAsync(
        MaintenanceAlert alert,
        MaintenanceLog log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(log);

        await SendSignalRAsync(
            "alertstatuschanged",
            new
            {
                id = alert.Id,
                printerId = alert.PrinterId,
                status = alert.Status.ToString(),
                resolvedAt = alert.ResolvedAt,
                resolvedBy = alert.ResolvedBy
            },
            alert.Id,
            cancellationToken);

        await SendSignalRAsync(
            "maintenancecompleted",
            new
            {
                logId = log.Id,
                printerId = log.PrinterId,
                deploymentId = log.PrinterMaintenanceScheduleId,
                performedAt = log.PerformedAt,
                performedBy = log.PerformedBy
            },
            alert.Id,
            cancellationToken);

        try
        {
            webhookService.Enqueue("maintenance.completed", new
            {
                logId = log.Id,
                printerId = log.PrinterId,
                performedAt = log.PerformedAt,
                performedBy = log.PerformedBy
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Maintenance alert {AlertId} was resolved, but the completion webhook could not be queued.",
                alert.Id);
        }
    }

    private async Task SendSignalRAsync(
        string eventName,
        object payload,
        Guid alertId,
        CancellationToken cancellationToken)
    {
        try
        {
            await maintenanceHub.Clients.All.SendAsync(
                eventName,
                payload,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Maintenance alert {AlertId} was resolved, but SignalR event {EventName} failed.",
                alertId,
                eventName);
        }
    }
}
