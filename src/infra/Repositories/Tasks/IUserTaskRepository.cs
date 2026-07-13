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
    /// Gets all pending tasks, optionally filtered by type, optionally excluding
    /// maintenance-sourced tasks. Non-admin callers must pass
    /// <paramref name="includeMaintenance"/> = <c>false</c> so maintenance alert
    /// content is never surfaced to them (issue #713 Fix 8).
    /// </summary>
    Task<IReadOnlyList<UserTask>> GetPendingTasksAsync(UserTaskType? taskType, bool includeMaintenance, CancellationToken ct = default);

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
    /// Gets the count of pending tasks, optionally filtered by type, optionally
    /// excluding maintenance-sourced tasks. Non-admin callers must pass
    /// <paramref name="includeMaintenance"/> = <c>false</c> so the count matches
    /// the filtered list they are allowed to see (issue #713 Fix 8).
    /// </summary>
    Task<int> GetPendingCountAsync(UserTaskType? taskType, bool includeMaintenance, CancellationToken ct = default);

    /// <summary>
    /// Returns the (SourceKind, SourceId) keys of compiler-owned tasks a user
    /// recently Skipped or Dismissed (UpdatedAt &gt;= <paramref name="updatedAfterUtc"/>).
    /// The shift-plan compiler consults this set to avoid resurrecting a task the
    /// user explicitly cleared until the suppression window lapses (issue #713 Fix F).
    /// </summary>
    Task<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>> GetSuppressedSourceKeysAsync(
        DateTime updatedAfterUtc, CancellationToken ct = default);

    /// <summary>
    /// Returns suppressed compiler task source keys that match the currently-active
    /// source keys. By default, rows older than 30 days are excluded so ancient terminal
    /// rows cannot be rehydrated as current episodes. Used for each source kind until
    /// that source successfully evaluates after compiler process start.
    /// </summary>
    Task<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>> GetOpenSuppressedByKeysAsync(
        IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)> activeKeys,
        DateTime? maxAgeUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a new task.
    /// </summary>
    Task AddAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Tracks a new task in the change tracker without saving. Call
    /// <see cref="SaveChangesAsync"/> after batching multiple adds/updates.
    /// </summary>
    Task TrackAddAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing task.
    /// </summary>
    Task UpdateAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Tracks an update in the change tracker without saving. Call
    /// <see cref="SaveChangesAsync"/> after batching multiple adds/updates.
    /// </summary>
    Task TrackUpdateAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Updates only the named properties of a task, without requiring the caller to
    /// hold a tracked entity. User-driven mutations (complete/dismiss/skip) load via
    /// <see cref="GetByIdAsync"/> (no-tracking), so a blind full-entity
    /// <see cref="UpdateAsync"/> can clobber columns changed concurrently by another
    /// writer (e.g. the shift-plan compiler) that the caller never touched. Always
    /// stamps <see cref="UserTask.UpdatedAt"/> in addition to the named properties
    /// (issue #713 Fix R3-5).
    /// </summary>
    Task UpdateFieldsAsync(UserTask task, IReadOnlyCollection<string> propertyNames, CancellationToken ct = default);

    /// <summary>
    /// Atomically updates only the named properties when the row is still open
    /// (<see cref="UserTaskStatus.Pending"/> or <see cref="UserTaskStatus.InProgress"/>).
    /// Returns <c>false</c> without changing the row if a concurrent action moved it
    /// to a terminal status.
    /// </summary>
    Task<bool> TryUpdateFieldsIfOpenAsync(UserTask task, IReadOnlyCollection<string> propertyNames, CancellationToken ct = default);

    /// <summary>
    /// Atomically updates only the named properties when the row is still open and
    /// still has the expected <see cref="UserTask.UpdatedAt"/> timestamp. Returns
    /// <c>false</c> without changing the row if a concurrent action changed the row
    /// or moved it to a terminal status.
    /// </summary>
    Task<bool> TryUpdateFieldsIfOpenAsync(
        UserTask task,
        IReadOnlyCollection<string> propertyNames,
        DateTime? expectedUpdatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically completes the task with <paramref name="taskId"/> only if its
    /// current database status is still <see cref="UserTaskStatus.Pending"/> or
    /// <see cref="UserTaskStatus.InProgress"/>. Returns <c>false</c> (without making
    /// any change) if a concurrent user action already moved the task to a terminal
    /// state (Skipped/Dismissed/Completed) — that state wins the race instead of
    /// being silently overwritten by the shift-plan compiler's auto-complete pass
    /// (issue #713 Fix R3-5).
    /// </summary>
    Task<bool> TryAutoCompleteAsync(Guid taskId, DateTime completedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Detaches the given tasks from the change tracker without saving. Used after a
    /// direct/conditional write (e.g. <see cref="TryAutoCompleteAsync"/>) or after a
    /// caught <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> so a
    /// subsequent <see cref="SaveChangesAsync"/> does not redundantly (or
    /// incorrectly) attempt to persist the same entities again (issue #713 Fix R3-2,
    /// Fix R3-5).
    /// </summary>
    Task DetachTrackedAsync(IEnumerable<UserTask> tasks, CancellationToken ct = default);

    /// <summary>
    /// Deletes a task.
    /// </summary>
    Task DeleteAsync(UserTask task, CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
