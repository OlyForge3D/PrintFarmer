using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// SignalR-based progress notifier for slicer operations
/// </summary>
public class SignalRSlicerProgressNotifier : ISlicerProgressNotifier
{
    private readonly IHubContext<SlicerProgressHub> _hubContext;
    private readonly ILogger<SignalRSlicerProgressNotifier> _logger;
    private readonly Dictionary<Guid, HashSet<string>> _jobSubscriptions = [];
    private readonly object _lockObject = new();

    public SignalRSlicerProgressNotifier(
        IHubContext<SlicerProgressHub> hubContext,
        ILogger<SignalRSlicerProgressNotifier> logger)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task NotifyProgressAsync(SlicingProgressUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            // Get subscribers for this job
            var connectionIds = GetJobSubscribers(update.JobId);

            if (connectionIds.Count > 0)
            {
                // Send to specific subscribers
                await _hubContext.Clients.Clients(connectionIds).SendAsync("SlicingProgress", update, cancellationToken);
                _logger.LogDebug("Sent progress update for job {JobId} to {SubscriberCount} subscribers: {Progress}%",
                    update.JobId, connectionIds.Count, update.Progress);
            }

            // Also send to a general group for monitoring dashboards
            await _hubContext.Clients.Group("SlicingMonitors").SendAsync("SlicingProgress", update, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify progress for job {JobId}", update.JobId);
            throw;
        }
    }

    public async Task NotifyCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var completionNotification = new SlicingCompletionNotification
            {
                JobId = job.Id,
                UserId = job.UserId,
                Status = job.Status,
                Success = result.Success,
                ResultFileUrl = string.IsNullOrWhiteSpace(result.ResultFileUrl) ? null : new Uri(result.ResultFileUrl, UriKind.RelativeOrAbsolute),
                ProcessingTimeSeconds = result.ProcessingTimeSeconds,
                EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds,
                EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams,
                LayerCount = result.LayerCount,
                ErrorMessage = result.Error,
                CompletedAt = job.CompletedAt ?? DateTime.UtcNow
            };

            foreach (var kv in job.Metadata)
            {
                completionNotification.Metadata[kv.Key] = kv.Value;
            }

            // Get subscribers for this job
            var connectionIds = GetJobSubscribers(job.Id);

            if (connectionIds.Count > 0)
            {
                // Send to specific subscribers
                await _hubContext.Clients.Clients(connectionIds).SendAsync("SlicingCompleted", completionNotification, cancellationToken);
                _logger.LogInformation("Sent completion notification for job {JobId} to {SubscriberCount} subscribers",
                    job.Id, connectionIds.Count);
            }

            // Send to user's personal group
            await _hubContext.Clients.Group($"User-{job.UserId}").SendAsync("SlicingCompleted", completionNotification, cancellationToken);

            // Send to monitoring group
            await _hubContext.Clients.Group("SlicingMonitors").SendAsync("SlicingCompleted", completionNotification, cancellationToken);

            // Clean up subscriptions for completed job
            RemoveJobSubscriptions(job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify completion for job {JobId}", job.Id);
            throw;
        }
    }

    public async Task NotifyFailureAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            var failureNotification = new SlicingFailureNotification
            {
                JobId = job.Id,
                UserId = job.UserId,
                Status = job.Status,
                ErrorMessage = errorMessage,
                FailedAt = job.CompletedAt ?? DateTime.UtcNow,
                RetryCount = job.RetryCount,
                CanRetry = job.RetryCount < 3
            };

            foreach (var kv in job.Metadata)
            {
                failureNotification.Metadata[kv.Key] = kv.Value;
            }

            // Get subscribers for this job
            var connectionIds = GetJobSubscribers(job.Id);

            if (connectionIds.Count > 0)
            {
                // Send to specific subscribers
                await _hubContext.Clients.Clients(connectionIds).SendAsync("SlicingFailed", failureNotification, cancellationToken);
                _logger.LogInformation("Sent failure notification for job {JobId} to {SubscriberCount} subscribers",
                    job.Id, connectionIds.Count);
            }

            // Send to user's personal group
            await _hubContext.Clients.Group($"User-{job.UserId}").SendAsync("SlicingFailed", failureNotification, cancellationToken);

            // Send to monitoring group
            await _hubContext.Clients.Group("SlicingMonitors").SendAsync("SlicingFailed", failureNotification, cancellationToken);

            // Clean up subscriptions for failed job (unless it can be retried)
            if (!failureNotification.CanRetry)
            {
                RemoveJobSubscriptions(job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify failure for job {JobId}", job.Id);
            throw;
        }
    }

    public Task SubscribeToJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (!_jobSubscriptions.TryGetValue(jobId, out var set))
            {
                set = [];
                _jobSubscriptions[jobId] = set;
            }

            set.Add(connectionId);
        }

        _logger.LogDebug("Added subscription for job {JobId} from connection {ConnectionId}", jobId, connectionId);
        return Task.CompletedTask;
    }

    public Task UnsubscribeFromJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (_jobSubscriptions.TryGetValue(jobId, out var set))
            {
                set.Remove(connectionId);
                if (set.Count == 0)
                {
                    _jobSubscriptions.Remove(jobId);
                }
            }
        }

        _logger.LogDebug("Removed subscription for job {JobId} from connection {ConnectionId}", jobId, connectionId);
        return Task.CompletedTask;
    }

    private List<string> GetJobSubscribers(Guid jobId)
    {
        lock (_lockObject)
        {
            if (_jobSubscriptions.TryGetValue(jobId, out var subscribers))
            {
                return [.. subscribers];
            }
            return [];
        }
    }

    private void RemoveJobSubscriptions(Guid jobId)
    {
        lock (_lockObject)
        {
            _jobSubscriptions.Remove(jobId);
        }
    }
}

/// <summary>
/// SignalR Hub for slicer progress updates
/// </summary>
public class SlicerProgressHub : Hub
{
    private readonly ILogger<SlicerProgressHub> _logger;
    private readonly ISlicerProgressNotifier _progressNotifier;

    public SlicerProgressHub(ILogger<SlicerProgressHub> logger, ISlicerProgressNotifier progressNotifier)
    {
        _logger = logger;
        _progressNotifier = progressNotifier;
    }

    public async Task SubscribeToJobAsync(Guid jobId)
    {
        await _progressNotifier.SubscribeToJobAsync(jobId, Context.ConnectionId);
        _logger.LogDebug("Connection {ConnectionId} subscribed to job {JobId}", Context.ConnectionId, jobId);
    }

    public async Task UnsubscribeFromJobAsync(Guid jobId)
    {
        await _progressNotifier.UnsubscribeFromJobAsync(jobId, Context.ConnectionId);
        _logger.LogDebug("Connection {ConnectionId} unsubscribed from job {JobId}", Context.ConnectionId, jobId);
    }

    public async Task JoinUserGroupAsync(Guid userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"User-{userId}");
        _logger.LogDebug("Connection {ConnectionId} joined user group {UserId}", Context.ConnectionId, userId);
    }

    public async Task LeaveUserGroupAsync(Guid userId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"User-{userId}");
        _logger.LogDebug("Connection {ConnectionId} left user group {UserId}", Context.ConnectionId, userId);
    }

    public async Task JoinMonitoringGroupAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "SlicingMonitors");
        _logger.LogDebug("Connection {ConnectionId} joined monitoring group", Context.ConnectionId);
    }

    public async Task LeaveMonitoringGroupAsync()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "SlicingMonitors");
        _logger.LogDebug("Connection {ConnectionId} left monitoring group", Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Clean up any job subscriptions for this connection
        // This is a simplified cleanup - in production you might want to track subscriptions per connection
        _logger.LogDebug("Connection {ConnectionId} disconnected", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Notification sent when a slicing job completes successfully
/// </summary>
public class SlicingCompletionNotification
{
    public Guid JobId { get; set; }
    public Guid UserId { get; set; }
    public SlicingJobStatus Status { get; set; }
    public bool Success { get; set; }
    public Uri? ResultFileUrl { get; set; }
    public double ProcessingTimeSeconds { get; set; }
    public double EstimatedPrintTimeSeconds { get; set; }
    public double EstimatedFilamentUsageGrams { get; set; }
    public int LayerCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CompletedAt { get; set; }
    public Dictionary<string, object> Metadata { get; } = [];
}

/// <summary>
/// Notification sent when a slicing job fails
/// </summary>
public class SlicingFailureNotification
{
    public Guid JobId { get; set; }
    public Guid UserId { get; set; }
    public SlicingJobStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime FailedAt { get; set; }
    public int RetryCount { get; set; }
    public bool CanRetry { get; set; }
    public Dictionary<string, object> Metadata { get; } = [];
}