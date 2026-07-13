using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Tasks;

/// <summary>
/// Repository abstraction for querying and mutating user tasks.
/// </summary>
public interface IUserTaskRepository
{
    /// <summary>
    /// Gets a task by its unique identifier.
    /// </summary>
    Task<UserTask?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all pending tasks, optionally filtered by type.
    /// </summary>
    Task<IReadOnlyList<UserTask>> GetPendingTasksAsync(UserTaskType? taskType = null, CancellationToken ct = default);

    /// <summary>
    /// Gets all tasks with specified statuses.
    /// </summary>
    Task<IReadOnlyList<UserTask>> GetByStatusAsync(IEnumerable<UserTaskStatus> statuses, CancellationToken ct = default);

    /// <summary>
    /// Gets a task by entity type and entity ID.
    /// Used to check if a task already exists for an entity (e.g., ProfileImport for a PrinterModel).
    /// </summary>
    Task<UserTask?> GetByEntityAsync(UserTaskType taskType, string entityType, Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Gets the open (Pending or InProgress) task matching a canonical shift-plan
    /// source. Returns <c>null</c> when no such task exists. Used by the
    /// shift-plan compiler to dedupe by (<see cref="UserTaskSourceKind"/>,
    /// <see cref="UserTask.SourceId"/>).
    /// </summary>
    Task<UserTask?> GetOpenBySourceAsync(UserTaskSourceKind sourceKind, string sourceId, CancellationToken ct = default);

    /// <summary>
    /// Returns every open task materialized by the shift-plan compiler
    /// (SourceKind ≠ <see cref="UserTaskSourceKind.Unspecified"/>). Used by the
    /// compiler to detect tasks whose source has since resolved so they can be
    /// auto-completed.
    /// </summary>
    Task<IReadOnlyList<UserTask>> GetOpenCompilerTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the count of pending tasks, optionally filtered by type.
    /// </summary>
    Task<int> GetPendingCountAsync(UserTaskType? taskType = null, CancellationToken ct = default);

    /// <summary>
    /// Adds a new task.
    /// </summary>
    Task AddAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing task.
    /// </summary>
    Task UpdateAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Deletes a task.
    /// </summary>
    Task DeleteAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
