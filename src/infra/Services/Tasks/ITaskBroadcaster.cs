using Farm.Infrastructure.Services.Tasks;

namespace Farm.Infrastructure.Services.Tasks;

/// <summary>
/// Interface for broadcasting task events to connected clients.
/// Implementation details (SignalR, etc.) are handled in the API layer.
/// </summary>
public interface ITaskBroadcaster
{
    /// <summary>
    /// Broadcasts when a new task is created.
    /// </summary>
    /// <param name="task">The created task.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BroadcastTaskCreatedAsync(UserTaskDto task, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts when a task is updated (status change, etc.).
    /// </summary>
    /// <param name="task">The updated task.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BroadcastTaskUpdatedAsync(UserTaskDto task, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts when the pending task count changes.
    /// </summary>
    /// <param name="count">The new pending task count.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BroadcastPendingTaskCountAsync(int count, CancellationToken ct = default);
}
