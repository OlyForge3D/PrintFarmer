using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Tasks;

/// <summary>
/// SignalR implementation of task broadcaster.
/// Broadcasts task events to all connected clients using SignalR hubs.
/// </summary>
public class SignalRTaskBroadcaster(IHubContext<PrinterHub> hubContext) : ITaskBroadcaster
{
    private readonly IHubContext<PrinterHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    /// <inheritdoc />
    public async Task BroadcastTaskCreatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        await _hubContext.Clients.All.SendAsync("taskcreated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastTaskUpdatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        await _hubContext.Clients.All.SendAsync("taskupdated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastPendingTaskCountAsync(int count, CancellationToken ct = default)
    {
        await _hubContext.Clients.All.SendAsync("pendingtaskcount", new { count }, ct);
    }
}
