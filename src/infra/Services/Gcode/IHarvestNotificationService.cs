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
    Task NotifyFileUpdatedAsync(Guid operationId, object fileDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about file progress.
    /// </summary>
    Task NotifyFileProgressAsync(Guid operationId, string fileName, long bytesCopied, long totalBytes, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about harvest operation progress.
    /// </summary>
    Task NotifyOperationProgressAsync(Guid operationId, object progressDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about a discovered file being added.
    /// </summary>
    Task NotifyFileDiscoveredAsync(Guid operationId, object fileDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about harvest operation cancellation.
    /// </summary>
    Task NotifyOperationCancelledAsync(Guid operationId, object cancelledDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about harvest discovery being restarted.
    /// </summary>
    Task NotifyDiscoveryRestartedAsync(Guid operationId, object restartedDto, CancellationToken ct = default);

    /// <summary>
    /// Notify clients about operation completion.
    /// </summary>
    Task NotifyOperationCompletedAsync(Guid operationId, object completedDto, CancellationToken ct = default);
}
