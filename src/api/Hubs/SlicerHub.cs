using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Hubs;

/// <summary>
/// SignalR hub for broadcasting slicer registry events in real-time
/// </summary>
public class SlicerHub : Hub
{
    private readonly ILogger<SlicerHub> _logger;

    public SlicerHub(ILogger<SlicerHub> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Client connected to SlicerHub: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client disconnected from SlicerHub: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client can call this to request current registry state
    /// </summary>
    public async Task RequestRegistryUpdateAsync()
    {
        _logger.LogDebug("Client {ConnectionId} requested registry update", Context.ConnectionId);
        // The client should call GET /api/slicers after receiving this acknowledgment
        await Clients.Caller.SendAsync("RegistryUpdateRequested");
    }
}

/// <summary>
/// Events broadcast by the SlicerHub (called from SlicersService)
/// </summary>
public static class SlicerHubEvents
{
    /// <summary>
    /// Event name for when a new slicer service registers
    /// Payload: { id: Guid, name: string, slicerType: int, version: string, capabilities: string[] }
    /// </summary>
    public const string SlicerRegistered = "SlicerRegistered";

    /// <summary>
    /// Event name for when a slicer service sends a heartbeat
    /// Payload: { id: Guid, status: string, freeSlots: int, lastSeen: DateTime }
    /// </summary>
    public const string SlicerHeartbeat = "SlicerHeartbeat";

    /// <summary>
    /// Event name for when a slicer service deregisters
    /// Payload: { id: Guid, name: string }
    /// </summary>
    public const string SlicerDeregistered = "SlicerDeregistered";

    /// <summary>
    /// Event name for when a slicer service rotates its API key
    /// Payload: { id: Guid, name: string, rotatedAt: DateTime }
    /// </summary>
    public const string SlicerApiKeyRotated = "SlicerApiKeyRotated";

    /// <summary>
    /// Event name for when profile import starts
    /// Payload: { totalProfiles: int, message: string }
    /// </summary>
    public const string ProfileImportStarted = "ProfileImportStarted";

    /// <summary>
    /// Event name for when a profile is imported
    /// Payload: { profileName: string, profileType: string, count: int, total: int }
    /// </summary>
    public const string ProfileImported = "ProfileImported";

    /// <summary>
    /// Event name for when profile import completes
    /// Payload: { imported: int, skipped: int, deleted: int, message: string }
    /// </summary>
    public const string ProfileImportCompleted = "ProfileImportCompleted";

    /// <summary>
    /// Event name for when profile import encounters an error
    /// Payload: { error: string, profileName: string }
    /// </summary>
    public const string ProfileImportError = "ProfileImportError";
}
