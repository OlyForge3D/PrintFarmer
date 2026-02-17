using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Hubs;

/// <summary>
/// SignalR Hub for slicer progress updates.
/// Mapped to /hubs/slicers.
/// </summary>
public class SlicerProgressHub(
    ILogger<SlicerProgressHub> logger,
    ISlicerProgressNotifier progressNotifier) : Hub
{
    private readonly ILogger<SlicerProgressHub> _logger = logger;
    private readonly ISlicerProgressNotifier _progressNotifier = progressNotifier;

    /// <summary>
    /// Subscribe to progress updates for a specific slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    public async Task SubscribeToJobAsync(Guid jobId)
    {
        await _progressNotifier.SubscribeToJobAsync(jobId, Context.ConnectionId);
        _logger.LogDebug("Connection {ConnectionId} subscribed to job {JobId}", Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Unsubscribe from progress updates for a specific slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    public async Task UnsubscribeFromJobAsync(Guid jobId)
    {
        await _progressNotifier.UnsubscribeFromJobAsync(jobId, Context.ConnectionId);
        _logger.LogDebug("Connection {ConnectionId} unsubscribed from job {JobId}", Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Join a user group to receive all updates for a specific user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public async Task JoinUserGroupAsync(Guid userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"User-{userId}");
        _logger.LogDebug("Connection {ConnectionId} joined user group {UserId}", Context.ConnectionId, userId);
    }

    /// <summary>
    /// Leave a user group.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public async Task LeaveUserGroupAsync(Guid userId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"User-{userId}");
        _logger.LogDebug("Connection {ConnectionId} left user group {UserId}", Context.ConnectionId, userId);
    }

    /// <summary>
    /// Join the monitoring group to receive all slicing progress events.
    /// </summary>
    public async Task JoinMonitoringGroupAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "SlicingMonitors");
        _logger.LogDebug("Connection {ConnectionId} joined monitoring group", Context.ConnectionId);
    }

    /// <summary>
    /// Leave the monitoring group.
    /// </summary>
    public async Task LeaveMonitoringGroupAsync()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "SlicingMonitors");
        _logger.LogDebug("Connection {ConnectionId} left monitoring group", Context.ConnectionId);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Connection {ConnectionId} disconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
