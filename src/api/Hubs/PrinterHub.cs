using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Hubs;

public class PrinterHub : Hub
{
    // Marker hub for broadcasting printer updates and discovery progress.

    // Group management for discovery sessions
    public async Task JoinDiscoveryGroupAsync(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
    }

    public async Task LeaveDiscoveryGroupAsync(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
    }
}
