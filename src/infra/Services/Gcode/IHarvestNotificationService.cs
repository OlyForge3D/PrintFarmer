using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Interface for harvest-related SignalR notifications.
/// Services use this to notify clients without direct IHubContext dependency.
/// </summary>
public interface IHarvestNotificationService
{
    /// <summary>
    /// Notify clients about a discovered file being updated.
    /// </summary>
    /// <param name="operationId">The unique identifier of the harvest operation.</param>
    /// <param name="fileDto">The file data transfer object containing updated file information.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    Task NotifyFileUpdatedAsync(Guid operationId, object fileDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about file progress.
    /// </summary>
    /// <param name="operationId">The unique identifier of the harvest operation.</param>
    /// <param name="fileName">The name of the file being processed.</param>
    /// <param name="bytesCopied">The number of bytes copied so far.</param>
    /// <param name="totalBytes">The total number of bytes to copy.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    Task NotifyFileProgressAsync(Guid operationId, string fileName, long bytesCopied, long totalBytes, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about harvest operation progress.
    /// </summary>
    /// <param name="operationId">The unique identifier of the harvest operation.</param>
    /// <param name="progressDto">The progress data transfer object containing operation progress details.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    Task NotifyOperationProgressAsync(Guid operationId, object progressDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about a discovered file being added.
    /// </summary>
    /// <param name="operationId">The unique identifier of the harvest operation.</param>
    /// <param name="fileDto">The file data transfer object containing discovered file information.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    Task NotifyFileDiscoveredAsync(Guid operationId, object fileDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about harvest operation cancellation.
    /// </summary>
    /// <param name="operationId">The unique identifier of the harvest operation.</param>
    /// <param name="cancelledDto">The data transfer object containing cancellation details.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    Task NotifyOperationCancelledAsync(Guid operationId, object cancelledDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about harvest discovery being restarted.
    /// </summary>
    /// <param name="operationId">The unique identifier of the harvest operation.</param>
    /// <param name="restartedDto">The data transfer object containing restart details.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    Task NotifyDiscoveryRestartedAsync(Guid operationId, object restartedDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about operation completion.
    /// </summary>
    /// <param name="operationId">The unique identifier of the harvest operation.</param>
    /// <param name="completedDto">The data transfer object containing completion details.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    Task NotifyOperationCompletedAsync(Guid operationId, object completedDto, CancellationToken ct = default);
}
