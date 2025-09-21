using Farm.Web.Api.Services;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Hubs;

public class PrinterHub(IDiscoveryProgressCache progressCache) : Hub
{
    // Marker hub for broadcasting printer updates and discovery progress.

    // Group management for discovery sessions
    public async Task JoinDiscoveryGroupAsync(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
        // After joining, replay latest cached progress if available. There is a narrow race where the
        // controller returns the session id before the discovery service has published & cached the
        // initial progress snapshot. To mitigate, perform a brief bounded retry.
        for (int i = 0; i < 5; i++)
        {
            if (progressCache.TryGet(sessionId, out DiscoveryProgressDto? progress) && progress != null)
            {
                await Clients.Caller.SendAsync("discoveryprogress", progress);
                break;
            }
            // If cancelled/connection aborted stop early
            if (Context.ConnectionAborted.IsCancellationRequested)
            {
                break;
            }
            await Task.Delay(100, Context.ConnectionAborted);
        }
    }

    public async Task LeaveDiscoveryGroupAsync(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"discovery-{sessionId}");
    }
}
