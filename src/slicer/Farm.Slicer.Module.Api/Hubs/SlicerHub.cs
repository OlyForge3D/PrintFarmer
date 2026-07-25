using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Hubs;

/// <summary>
/// Well-known SignalR event names for the slicer hub.
/// </summary>
public static class SlicerHubEvents
{
    /// <summary>Event name for when a new slicer service registers.</summary>
    public const string SlicerRegistered = "SlicerRegistered";

    /// <summary>Event name for when a slicer service sends a heartbeat.</summary>
    public const string SlicerHeartbeat = "SlicerHeartbeat";

    /// <summary>Event name for when a slicer service deregisters.</summary>
    public const string SlicerDeregistered = "SlicerDeregistered";

    /// <summary>Event name for when a slicer service rotates its API key.</summary>
    public const string SlicerApiKeyRotated = "SlicerApiKeyRotated";

    /// <summary>Event name for when profile import starts.</summary>
    public const string ProfileImportStarted = "ProfileImportStarted";

    /// <summary>Event name for when profile import completes.</summary>
    public const string ProfileImportCompleted = "ProfileImportCompleted";

    /// <summary>Event name for when profile import fails.</summary>
    public const string ProfileImportFailed = "ProfileImportFailed";

    /// <summary>Event for a registry update request.</summary>
    public const string RegistryUpdate = "RegistryUpdate";
}

/// <summary>
/// SignalR hub for slicer service registration and status.
/// Mapped to /hubs/slicer-registry.
/// </summary>
public class SlicerHub(ILogger<SlicerHub> logger) : Hub
{
    private readonly ILogger<SlicerHub> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SlicerHub client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "SlicerHub client disconnected: {ConnectionId}, Reason: {Reason}",
            Context.ConnectionId,
            exception?.Message ?? "Normal");
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Join a slicer service group to receive targeted updates.
    /// </summary>
    /// <param name="serviceId">The slicer service ID.</param>
    public async Task JoinServiceGroupAsync(string serviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"slicer-{serviceId}");
        _logger.LogDebug("Client {ConnectionId} joined slicer group: {ServiceId}", Context.ConnectionId, serviceId);
    }

    /// <summary>
    /// Leave a slicer service group.
    /// </summary>
    /// <param name="serviceId">The slicer service ID.</param>
    public async Task LeaveServiceGroupAsync(string serviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"slicer-{serviceId}");
        _logger.LogDebug("Client {ConnectionId} left slicer group: {ServiceId}", Context.ConnectionId, serviceId);
    }

    /// <summary>
    /// Join the slicing progress group to receive job progress updates.
    /// </summary>
    public async Task JoinProgressGroupAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "slicing-progress");
        _logger.LogDebug("Client {ConnectionId} joined slicing-progress group", Context.ConnectionId);
    }

    /// <summary>
    /// Leave the slicing progress group.
    /// </summary>
    public async Task LeaveProgressGroupAsync()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "slicing-progress");
        _logger.LogDebug("Client {ConnectionId} left slicing-progress group", Context.ConnectionId);
    }

    /// <summary>
    /// Join a specific job's progress group.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    public async Task JoinJobGroupAsync(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"job-{jobId}");
        _logger.LogDebug("Client {ConnectionId} joined job group: {JobId}", Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Leave a specific job's progress group.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    public async Task LeaveJobGroupAsync(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job-{jobId}");
        _logger.LogDebug("Client {ConnectionId} left job group: {JobId}", Context.ConnectionId, jobId);
    }
}
