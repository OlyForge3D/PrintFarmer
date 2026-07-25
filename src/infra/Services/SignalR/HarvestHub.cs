using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// SignalR hub for broadcasting G-code harvest progress and operations.
/// </summary>
public class HarvestHub : Hub
{
    // Called by backend to broadcast per-file progress to all clients in the operation group
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

    // Clients join a group for a specific harvest operation
    public async Task JoinHarvestGroupAsync(Guid operationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"harvest-{operationId}");
    }

    public async Task LeaveHarvestGroupAsync(Guid operationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"harvest-{operationId}");
    }
}
