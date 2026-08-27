using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Modules.Observability.Services.Tasks;

/// <summary>
/// SignalR implementation of task broadcaster.
/// Broadcasts task events to their least-privileged authenticated audience.
/// </summary>
/// <remarks>
/// Maintenance-sourced DTOs are restricted to authenticated administrators because
/// non-admin users must not receive their contents.
/// </remarks>
public class SignalRTaskBroadcaster(IHubContext<PrinterHub> hubContext) : ITaskBroadcaster
{
    private readonly IHubContext<PrinterHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    /// <inheritdoc />
    public async Task BroadcastTaskCreatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group(GetAudience(task))
            .SendAsync("taskcreated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastTaskUpdatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group(GetAudience(task))
            .SendAsync("taskupdated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastPendingTaskCountAsync(int count, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm)
            .SendAsync("pendingtaskcount", new { count }, ct);
    }

    private static string GetAudience(UserTaskDto task) =>
        task.SourceKind == UserTaskSourceKind.Maintenance
            ? PrinterHub.AdminTaskGroup
            : Farm.Infrastructure.Security.AuthorizedHubGroups.Farm;
}
