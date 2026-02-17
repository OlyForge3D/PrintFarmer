using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// SignalR-based progress notifier for slicer operations
/// </summary>
public class SignalRSlicerProgressNotifier(
    IHubContext<SlicerProgressHub> hubContext,
    IUnifiedLoggingService logger) : ISlicerProgressNotifier
{
    private readonly IHubContext<SlicerProgressHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Dictionary<Guid, HashSet<string>> _jobSubscriptions = [];
    private readonly Lock _lockObject = new();

    public async Task NotifyProgressAsync(SlicingProgressUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            // Get subscribers for this job
            List<string> connectionIds = GetJobSubscribers(update.JobId);

            if (connectionIds.Count > 0)
            {
                // Send to specific subscribers
                await _hubContext.Clients.Clients(connectionIds).SendAsync("slicingprogress", update, cancellationToken);
                _logger.LogDebug($"Sent progress update for job {update.JobId} to {connectionIds.Count} subscribers: {update.Progress}%");
            }

            // Also send to a general group for monitoring dashboards
            await _hubContext.Clients.Group("SlicingMonitors").SendAsync("slicingprogress", update, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to notify progress for job {update.JobId}: {ex.Message}");
            throw;
        }
    }

    public async Task NotifyCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            SlicingCompletionNotification completionNotification = new()
            {
                JobId = job.Id,
                UserId = job.UserId,
                Status = job.Status,
                Success = result.Success,
                ResultFileUrl = result.ResultFileUrl,
                ProcessingTimeSeconds = result.ProcessingTimeSeconds,
                EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds,
                EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams,
                LayerCount = result.LayerCount,
                ErrorMessage = result.Error,
                CompletedAt = job.CompletedAt ?? DateTime.UtcNow
            };

            foreach (KeyValuePair<string, object> kv in job.Metadata)
            {
                completionNotification.Metadata[kv.Key] = kv.Value;
            }

            // Get subscribers for this job
            List<string> connectionIds = GetJobSubscribers(job.Id);

            if (connectionIds.Count > 0)
            {
                // Send to specific subscribers
                await _hubContext.Clients.Clients(connectionIds).SendAsync("slicingcompleted", completionNotification, cancellationToken);
                _logger.LogInformation($"Sent completion notification for job {job.Id} to {connectionIds.Count} subscribers");
            }

            // Send to user's personal group
            await _hubContext.Clients.Group($"User-{job.UserId}").SendAsync("slicingcompleted", completionNotification, cancellationToken);

            // Send to monitoring group
            await _hubContext.Clients.Group("SlicingMonitors").SendAsync("slicingcompleted", completionNotification, cancellationToken);

            // Clean up subscriptions for completed job
            RemoveJobSubscriptions(job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to notify completion for job {job.Id}: {ex.Message}");
            throw;
        }
    }

    public async Task NotifyFailureAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            SlicingFailureNotification failureNotification = new()
            {
                JobId = job.Id,
                UserId = job.UserId,
                Status = job.Status,
                ErrorMessage = errorMessage,
                FailedAt = job.CompletedAt ?? DateTime.UtcNow,
                RetryCount = job.RetryCount,
                CanRetry = job.RetryCount < 3
            };

            foreach (KeyValuePair<string, object> kv in job.Metadata)
            {
                failureNotification.Metadata[kv.Key] = kv.Value;
            }

            // Get subscribers for this job
            List<string> connectionIds = GetJobSubscribers(job.Id);

            if (connectionIds.Count > 0)
            {
                // Send to specific subscribers
                await _hubContext.Clients.Clients(connectionIds).SendAsync("slicingfailed", failureNotification, cancellationToken);
                _logger.LogInformation($"Sent failure notification for job {job.Id} to {connectionIds.Count} subscribers");
            }

            // Send to user's personal group
            await _hubContext.Clients.Group($"User-{job.UserId}").SendAsync("slicingfailed", failureNotification, cancellationToken);

            // Send to monitoring group
            await _hubContext.Clients.Group("SlicingMonitors").SendAsync("slicingfailed", failureNotification, cancellationToken);

            // Clean up subscriptions for failed job (unless it can be retried)
            if (!failureNotification.CanRetry)
            {
                RemoveJobSubscriptions(job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to notify failure for job {job.Id}: {ex.Message}");
            throw;
        }
    }

    public Task SubscribeToJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (!_jobSubscriptions.TryGetValue(jobId, out HashSet<string>? set))
            {
                set = [];
                _jobSubscriptions[jobId] = set;
            }

            _ = set.Add(connectionId);
        }

        _logger.LogDebug($"Added subscription for job {jobId} from connection {connectionId}");
        return Task.CompletedTask;
    }

    public Task UnsubscribeFromJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (_jobSubscriptions.TryGetValue(jobId, out HashSet<string>? set))
            {
                _ = set.Remove(connectionId);
                if (set.Count == 0)
                {
                    _ = _jobSubscriptions.Remove(jobId);
                }
            }
        }

        _logger.LogDebug($"Removed subscription for job {jobId} from connection {connectionId}");
        return Task.CompletedTask;
    }

    private List<string> GetJobSubscribers(Guid jobId)
    {
        lock (_lockObject)
        {
            return _jobSubscriptions.TryGetValue(jobId, out HashSet<string>? subscribers) ? [.. subscribers] : [];
        }
    }

    private void RemoveJobSubscriptions(Guid jobId)
    {
        lock (_lockObject)
        {
            _ = _jobSubscriptions.Remove(jobId);
        }
    }
}
