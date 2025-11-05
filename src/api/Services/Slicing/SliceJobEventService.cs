using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Service for broadcasting SliceJob lifecycle events via SignalR
/// </summary>
public interface ISliceJobEventService
{
    /// <summary>
    /// Broadcast event when a job is queued
    /// </summary>
    Task NotifyJobQueuedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job starts processing
    /// </summary>
    Task NotifyJobStartedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast progress update for a running job
    /// </summary>
    Task NotifyJobProgressAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job completes successfully
    /// </summary>
    Task NotifyJobCompletedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job fails
    /// </summary>
    Task NotifyJobFailedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job is cancelled
    /// </summary>
    Task NotifyJobCancelledAsync(SliceJob job, CancellationToken cancellationToken = default);
}

public class SliceJobEventService : ISliceJobEventService
{
    private readonly IHubContext<SlicerProgressHub> _hubContext;
    private readonly IUnifiedLoggingService _logger;

    public SliceJobEventService(
        IHubContext<SlicerProgressHub> hubContext,
        IUnifiedLoggingService logger)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
        await _hubContext.Clients.Group($"User-{userId}").SendAsync("SliceJobEvent", evt, cancellationToken);

        // Send to monitoring group (admin dashboards, etc.)
        await _hubContext.Clients.Group("SlicingMonitors").SendAsync("SliceJobEvent", evt, cancellationToken);
    }
}

/// <summary>
/// Event data for SliceJob lifecycle notifications
/// </summary>
public class SliceJobEvent
{
    /// <summary>
    /// Type of event: JobQueued, JobStarted, JobProgress, JobCompleted, JobFailed, JobCancelled
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Job ID
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// User who submitted the job
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Target printer ID (optional)
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Current job status
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Progress percentage (0-100) for JobProgress events
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Progress message for JobProgress events
    /// </summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// When the job was queued
    /// </summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// When processing started (if applicable)
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the job completed (if applicable)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// URL to result file for JobCompleted events
    /// </summary>
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// Error message for JobFailed events
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Estimated print time in seconds (for JobCompleted)
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>
    /// Estimated filament usage in grams (for JobCompleted)
    /// </summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// Worker ID that processed this job (if applicable)
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>
    /// Job priority (0=Low, 1=Normal, 2=High, 3=Critical)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// When this event was generated
    /// </summary>
    public DateTime Timestamp { get; set; }
}
