using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Hubs;

/// <summary>
/// SignalR Hub for slicer progress updates.
/// Mapped to /hubs/slicers.
/// </summary>
[Authorize]
public class SlicerProgressHub(
    ILogger<SlicerProgressHub> logger,
    ISlicerProgressNotifier progressNotifier,
    ISliceJobRepository jobRepository,
    ISlicerResourceAccessAuthorizer resourceAccess) : Hub
{
    private readonly ILogger<SlicerProgressHub> _logger = logger;
    private readonly ISlicerProgressNotifier _progressNotifier = progressNotifier;
    private readonly ISliceJobRepository _jobRepository = jobRepository;
    private readonly ISlicerResourceAccessAuthorizer _resourceAccess = resourceAccess;

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        if (!PrintFarmerPermissions.TryGetUserId(Context.User!, out Guid userId) ||
            !PrintFarmerPermissions.HasPermission(Context.User!, PrintFarmerPermissions.Queue.Read))
        {
            throw new HubException("authentication_required");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.User(userId));
        if (PrintFarmerPermissions.IsFarmAdmin(Context.User!))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.SlicingMonitors);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribe to progress updates for a specific slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    public async Task SubscribeToJobAsync(Guid jobId)
    {
        await EnsureJobAccessAsync(jobId);
        await _progressNotifier.SubscribeToJobAsync(jobId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.SliceJob(jobId));
        _logger.LogDebug("Connection {ConnectionId} subscribed to job {JobId}", Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Unsubscribe from progress updates for a specific slice job.
    /// </summary>
    /// <param name="jobId">The slice job ID.</param>
    public async Task UnsubscribeFromJobAsync(Guid jobId)
    {
        await _progressNotifier.UnsubscribeFromJobAsync(jobId, Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AuthorizedHubGroups.SliceJob(jobId));
        _logger.LogDebug("Connection {ConnectionId} unsubscribed from job {JobId}", Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Join a user group to receive all updates for a specific user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public async Task JoinUserGroupAsync(Guid userId)
    {
        if (!PrintFarmerPermissions.TryGetUserId(Context.User!, out Guid authenticatedUserId) ||
            (authenticatedUserId != userId && !PrintFarmerPermissions.IsFarmAdmin(Context.User!)))
        {
            throw new HubException("resource_forbidden");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.User(userId));
        _logger.LogDebug("Connection {ConnectionId} joined user group {UserId}", Context.ConnectionId, userId);
    }

    /// <summary>
    /// Leave a user group.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public async Task LeaveUserGroupAsync(Guid userId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AuthorizedHubGroups.User(userId));
        _logger.LogDebug("Connection {ConnectionId} left user group {UserId}", Context.ConnectionId, userId);
    }

    /// <summary>
    /// Join the monitoring group to receive all slicing progress events.
    /// </summary>
    public async Task JoinMonitoringGroupAsync()
    {
        if (!PrintFarmerPermissions.IsFarmAdmin(Context.User!))
        {
            throw new HubException("resource_forbidden");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.SlicingMonitors);
        _logger.LogDebug("Connection {ConnectionId} joined monitoring group", Context.ConnectionId);
    }

    /// <summary>
    /// Leave the monitoring group.
    /// </summary>
    public async Task LeaveMonitoringGroupAsync()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AuthorizedHubGroups.SlicingMonitors);
        _logger.LogDebug("Connection {ConnectionId} left monitoring group", Context.ConnectionId);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Connection {ConnectionId} disconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task EnsureJobAccessAsync(Guid jobId)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(jobId, Context.ConnectionAborted);
        if (job is null ||
            !_resourceAccess.CanAccess(Context.User!, job.UserId, "slice-job-hub", job.Id))
        {
            throw new HubException("resource_forbidden");
        }
    }
}
