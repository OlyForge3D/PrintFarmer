using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Service for broadcasting SliceJob lifecycle events via SignalR
/// </summary>
public interface ISliceJobEventService
{
    /// <summary>
    /// Broadcast event when a job is queued
    /// </summary>
    /// <param name="job">The slice job that was queued.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyJobQueuedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job starts processing
    /// </summary>
    /// <param name="job">The slice job that started processing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyJobStartedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast progress update for a running job
    /// </summary>
    /// <param name="job">The slice job with updated progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyJobProgressAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job completes successfully
    /// </summary>
    /// <param name="job">The slice job that completed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyJobCompletedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job fails
    /// </summary>
    /// <param name="job">The slice job that failed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyJobFailedAsync(SliceJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast event when a job is cancelled
    /// </summary>
    /// <param name="job">The slice job that was cancelled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyJobCancelledAsync(SliceJob job, CancellationToken cancellationToken = default);
}
