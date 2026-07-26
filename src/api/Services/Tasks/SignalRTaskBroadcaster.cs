using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Tasks;

/// <summary>
/// SignalR implementation of task broadcaster.
/// Broadcasts non-maintenance task events to authenticated farm clients.
/// </summary>
/// <remarks>
/// Maintenance-sourced DTOs remain REST-authoritative and are deliberately omitted
/// from the farm-wide event stream because non-admin users must not receive their
/// contents.
/// </remarks>
public class SignalRTaskBroadcaster(IHubContext<PrinterHub> hubContext) : ITaskBroadcaster
{
    private readonly IHubContext<PrinterHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    /// <inheritdoc />
    public async Task BroadcastTaskCreatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        if (IsMaintenanceOnlyBroadcast(task))
        {
            return;
        }

        await _hubContext.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm)
            .SendAsync("taskcreated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastTaskUpdatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        if (IsMaintenanceOnlyBroadcast(task))
        {
            return;
        }

        await _hubContext.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm)
            .SendAsync("taskupdated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastPendingTaskCountAsync(int count, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm)
            .SendAsync("pendingtaskcount", new { count }, ct);
    }

    private static bool IsMaintenanceOnlyBroadcast(UserTaskDto task)
        => task.SourceKind == UserTaskSourceKind.Maintenance;
}
