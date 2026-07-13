using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Tasks;

/// <summary>
/// SignalR implementation of task broadcaster.
/// Broadcasts task events to connected clients using SignalR hubs.
/// </summary>
/// <remarks>
/// Fix C (issue #713): maintenance-sourced task events carry alert content and are
/// broadcast only to the <see cref="PrinterHub.AdminTaskGroup"/> (admin connections),
/// never to <c>Clients.All</c>. This keeps the real-time channel consistent with the
/// REST gate that hides maintenance tasks from non-admins. All other task events keep
/// broadcasting to every connected client.
/// </remarks>
public class SignalRTaskBroadcaster(IHubContext<PrinterHub> hubContext) : ITaskBroadcaster
{
    private readonly IHubContext<PrinterHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    /// <inheritdoc />
    public async Task BroadcastTaskCreatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        await TargetFor(task).SendAsync("taskcreated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastTaskUpdatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        await TargetFor(task).SendAsync("taskupdated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastPendingTaskCountAsync(int count, CancellationToken ct = default)
    {
        // The bare count carries no maintenance content, so it stays a global broadcast.
        await _hubContext.Clients.All.SendAsync("pendingtaskcount", new { count }, ct);
    }

    // Fix C: maintenance DTOs go only to admin connections; everything else to all.
    private IClientProxy TargetFor(UserTaskDto task)
        => task.SourceKind == UserTaskSourceKind.Maintenance
            ? _hubContext.Clients.Group(PrinterHub.AdminTaskGroup)
            : _hubContext.Clients.All;
}
