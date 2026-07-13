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
/// Fix R3-3 (issue #713, round 3): <see cref="PrinterHub"/> is mapped with
/// <c>AllowAnonymous()</c> (see <c>Program.cs</c>), so <c>Context.User</c> is never an
/// authenticated <c>farm_admin</c> principal there and no connection can ever join
/// <see cref="PrinterHub.AdminTaskGroup"/> in practice — the React client does not
/// send an access token to this hub. Routing maintenance-sourced task DTOs to that
/// group (the round-2 "Fix C") was therefore broadcasting to a group with no
/// members: not a security hole (non-admins still receive nothing), but silently
/// misleading — it implied admins get live maintenance updates when none do.
/// <para>
/// Until an authenticated channel exists (tracked as a follow-up: add
/// <c>[Authorize]</c> plus <c>accessTokenFactory</c> wiring on the client, or a new
/// dedicated authenticated hub for task/maintenance events), maintenance-sourced task
/// DTOs are not broadcast over SignalR at all. This is a safe, explicit no-op — the
/// REST endpoints (<c>GET /api/tasks</c>, <c>GET /api/tasks/count</c> with the admin
/// flag) remain the authoritative source for maintenance content and are unaffected.
/// All other (non-maintenance) task events keep broadcasting to every connected
/// client, unchanged from before.
/// </para>
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

        await _hubContext.Clients.All.SendAsync("taskcreated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastTaskUpdatedAsync(UserTaskDto task, CancellationToken ct = default)
    {
        if (IsMaintenanceOnlyBroadcast(task))
        {
            return;
        }

        await _hubContext.Clients.All.SendAsync("taskupdated", task, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastPendingTaskCountAsync(int count, CancellationToken ct = default)
    {
        // Fix R3-4: callers (UserTaskService) now pass the non-maintenance-filtered
        // count, so this always agrees with the non-admin REST count. The bare
        // number carries no maintenance content either way, so it stays global.
        await _hubContext.Clients.All.SendAsync("pendingtaskcount", new { count }, ct);
    }

    // Fix R3-3: no reachable authenticated channel exists yet for maintenance DTOs
    // (see class remarks) — skip the broadcast entirely rather than routing to a
    // group no connection can join.
    private static bool IsMaintenanceOnlyBroadcast(UserTaskDto task)
        => task.SourceKind == UserTaskSourceKind.Maintenance;
}
