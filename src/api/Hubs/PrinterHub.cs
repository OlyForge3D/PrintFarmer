using Microsoft.AspNetCore.SignalR;
using Farm.Web.Api.Services;

namespace Farm.Web.Api.Hubs;

public class PrinterHub(IDiscoveryProgressCache progressCache) : Hub
{
    // Marker hub for broadcasting printer updates and discovery progress.

    // Group management for discovery sessions
    public async Task JoinDiscoveryGroupAsync(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
        // After joining, replay latest cached progress if available to mitigate race with initial emission
        if (progressCache.TryGet(sessionId, out var progress) && progress != null)
        {
            await Clients.Caller.SendAsync("DiscoveryProgress", progress);
        }
    }

    public async Task LeaveDiscoveryGroupAsync(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
    }
}
