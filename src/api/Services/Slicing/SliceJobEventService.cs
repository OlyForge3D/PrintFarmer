using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Slicing;

public class SliceJobEventService(
    IHubContext<SlicerProgressHub> hubContext,
    IUnifiedLoggingService logger) : ISliceJobEventService
{
    private readonly IHubContext<SlicerProgressHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task NotifyJobQueuedAsync(SliceJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        SliceJobEvent evt = new()
        {
            EventType = "JobQueued",
            JobId = job.Id,
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            QueuedAt = job.QueuedAt,
            Priority = job.Priority,
            Timestamp = DateTime.UtcNow
        };

        await BroadcastEventAsync(evt, job.UserId, cancellationToken);
        _logger.LogDebug($"Broadcasted JobQueued event for job {job.Id}");
    }

    public async Task NotifyJobStartedAsync(SliceJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        SliceJobEvent evt = new()
        {
            EventType = "JobStarted",
            JobId = job.Id,
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            WorkerId = job.WorkerId,
            Timestamp = DateTime.UtcNow
        };

        await BroadcastEventAsync(evt, job.UserId, cancellationToken);
        _logger.LogDebug($"Broadcasted JobStarted event for job {job.Id}");
    }

    public async Task NotifyJobProgressAsync(SliceJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        SliceJobEvent evt = new()
        {
            EventType = "JobProgress",
            JobId = job.Id,
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            ProgressPercent = job.ProgressPercent,
            ProgressMessage = job.ProgressMessage,
            Timestamp = DateTime.UtcNow
        };

        await BroadcastEventAsync(evt, job.UserId, cancellationToken);

        // Don't log progress updates at Debug level to avoid log spam
    }

    public async Task NotifyJobCompletedAsync(SliceJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        SliceJobEvent evt = new()
        {
            EventType = "JobCompleted",
            JobId = job.Id,
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ResultFileUrl = job.ResultFileUrl,
            EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = job.FilamentUsedGrams,
            WorkerId = job.WorkerId,
            Timestamp = DateTime.UtcNow
        };

        await BroadcastEventAsync(evt, job.UserId, cancellationToken);
        _logger.LogInformation($"Broadcasted JobCompleted event for job {job.Id}");
    }

    public async Task NotifyJobFailedAsync(SliceJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        SliceJobEvent evt = new()
        {
            EventType = "JobFailed",
            JobId = job.Id,
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            ErrorMessage = job.ErrorMessage,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            WorkerId = job.WorkerId,
            Timestamp = DateTime.UtcNow
        };

        await BroadcastEventAsync(evt, job.UserId, cancellationToken);
        _logger.LogWarning($"Broadcasted JobFailed event for job {job.Id}: {job.ErrorMessage}");
    }

    public async Task NotifyJobCancelledAsync(SliceJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        SliceJobEvent evt = new()
        {
            EventType = "JobCancelled",
            JobId = job.Id,
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Timestamp = DateTime.UtcNow
        };

        await BroadcastEventAsync(evt, job.UserId, cancellationToken);
        _logger.LogInformation($"Broadcasted JobCancelled event for job {job.Id}");
    }

    private async Task BroadcastEventAsync(SliceJobEvent evt, Guid userId, CancellationToken cancellationToken)
    {
        // Send to specific job subscribers
        await _hubContext.Clients.All.SendAsync($"SliceJob_{evt.JobId}", evt, cancellationToken);

        // Send to user group (all clients connected for this user)
        await _hubContext.Clients.Group($"User-{userId}").SendAsync("slicejobevent", evt, cancellationToken);

        // Send to monitoring group (admin dashboards, etc.)
        await _hubContext.Clients.Group("SlicingMonitors").SendAsync("slicejobevent", evt, cancellationToken);
    }
}
