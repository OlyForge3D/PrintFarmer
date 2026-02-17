using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Hubs;

/// <summary>
/// SignalR hub for slicer service registration and status.
/// Mapped to /hubs/slicer-registry.
/// </summary>
public class SlicerHub(ILogger<SlicerHub> logger) : Hub
{
    private readonly ILogger<SlicerHub> _logger = logger;

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
