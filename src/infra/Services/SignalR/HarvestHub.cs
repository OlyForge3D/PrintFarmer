using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// SignalR hub for broadcasting G-code harvest progress and operations.
///
/// Group membership is authorization-scoped (issue #2300): the REST surface for harvest data
/// is gated behind <c>gcode_harvest:admin</c> (see <c>GcodeHarvestController</c>), so this hub
/// must not deliver the same data to every authenticated connection. Both the farm-wide group
/// and any per-operation group require <c>gcode_harvest:admin</c> (or the <c>farm_admin</c>
/// role, which implies it via <see cref="PrintFarmerPermissions.HasPermission"/>) — mirroring
/// the fix already applied to <c>MaintenanceHub</c> (issue #1966).
/// </summary>
[Authorize]
public class HarvestHub : Hub
{
    private const string HarvestAdminPermission = "gcode_harvest:admin";

    public override async Task OnConnectedAsync()
    {
        // Farm-wide harvest data mirrors the REST gate on GcodeHarvestController
        // ([RequirePermission("gcode_harvest", "admin")]): only a caller who already holds
        // gcode_harvest:admin (or farm_admin, which implies it via HasPermission) is auto-joined
        // to the farm-wide group. Everyone else must not receive harvest metadata at all — harvest
        // is a farm-admin-only feature with no finer-grained resource scoping.
        if (PrintFarmerPermissions.HasPermission(Context.User!, HarvestAdminPermission))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm);
        }

        await base.OnConnectedAsync();
    }

    // Called by backend to broadcast per-file progress to all clients in the operation group
    [RequirePermission("gcode_harvest", "admin")]
    public async Task BroadcastFileProgressAsync(Guid operationId, string fileName, long bytesCopied, long totalBytes)
    {
        double percent = totalBytes > 0 ? (bytesCopied * 100.0 / totalBytes) : 0;
        await Clients.Group($"harvest-{operationId}").SendAsync("harvestfileprogress", new
        {
            operationId,
            fileName,
            bytesCopied,
            totalBytes,
            percent
        });
    }

    // Clients join a group for a specific harvest operation. Gated on the same gcode_harvest:admin
    // permission as the farm-wide group above — harvest has no finer-grained resource scoping, so
    // the permission check is sufficient (mirrors MaintenanceHub.SubscribeToPrinterAsync's throw
    // shape for an unauthorized join).
    public async Task JoinHarvestGroupAsync(Guid operationId)
    {
        if (!PrintFarmerPermissions.HasPermission(Context.User!, HarvestAdminPermission))
        {
            throw new HubException("resource_forbidden");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"harvest-{operationId}");
    }

    public async Task LeaveHarvestGroupAsync(Guid operationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"harvest-{operationId}");
    }
}
