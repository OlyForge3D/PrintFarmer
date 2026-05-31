using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// SignalR hub for broadcasting NFC tag scan events to connected clients.
/// Clients subscribe to nfctagread and nfctagunknown events.
/// </summary>
public class NfcHub(ILogger<NfcHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        logger.LogDebug("Client connected to NfcHub: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogDebug("Client disconnected from NfcHub: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// SignalR event names for NfcHub. All names are lowercase per SignalR conventions.
/// </summary>
public static class NfcHubEvents
{
    /// <summary>
    /// Emitted when a known tag is scanned.
    /// Payload: { tagUid, spoolId, spoolName, printerId, trayId, readAt }
    /// </summary>
    public const string TagRead = "nfctagread";

    /// <summary>
    /// Emitted when an unrecognized tag is scanned (no binding found).
    /// Payload: { tagUid, printerId, readAt }
    /// </summary>
    public const string TagUnknown = "nfctagunknown";
}
