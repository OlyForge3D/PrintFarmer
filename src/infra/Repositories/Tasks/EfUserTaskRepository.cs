using System.Linq.Expressions;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Tasks;

/// <summary>
/// Entity Framework implementation of user task repository.
/// </summary>
public class EfUserTaskRepository(AppDbContext db) : IUserTaskRepository
{
    private const int SuppressedSourceKeyBatchSize = 100;
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task<UserTask?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.UserTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<UserTask>> GetPendingTasksAsync(UserTaskType? taskType = null, CancellationToken ct = default)
        => GetPendingTasksAsync(taskType, includeMaintenance: true, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTask>> GetPendingTasksAsync(UserTaskType? taskType, bool includeMaintenance, CancellationToken ct = default)
    {
        IQueryable<UserTask> query = _db.UserTasks.AsNoTracking()
            .Where(t => t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress);

        if (taskType.HasValue)
        {
            query = query.Where(t => t.TaskType == taskType.Value);
        }

        if (!includeMaintenance)
        {
            // Fix 8/B: never surface maintenance alert content to non-admin callers.
            query = query.Where(t => t.SourceKind != UserTaskSourceKind.Maintenance);
        }

        return await query
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTask>> GetByStatusAsync(IEnumerable<UserTaskStatus> statuses, CancellationToken ct = default)
    {
        List<UserTaskStatus> statusList = statuses.ToList();
        return await _db.UserTasks.AsNoTracking()
            .Where(t => statusList.Contains(t.Status))
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<UserTask?> GetByEntityAsync(UserTaskType taskType, string entityType, Guid entityId, CancellationToken ct = default)
    {
        return await _db.UserTasks.AsNoTracking()
            .Where(t =>
                t.TaskType == taskType &&
                t.EntityType == entityType &&
                t.EntityId == entityId &&
                (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<UserTask?> GetOpenBySourceAsync(UserTaskSourceKind sourceKind, string sourceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        return await _db.UserTasks
            .Where(t =>
                t.SourceKind == sourceKind &&
                t.SourceId == sourceId &&
                (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTask>> GetOpenCompilerTasksAsync(CancellationToken ct = default)
    {
        List<UserTask> rows = await _db.UserTasks
            .Where(t =>
                t.SourceKind != UserTaskSourceKind.Unspecified &&
                (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress))
            .ToListAsync(ct);

        // Post-materialize filter: rows whose persisted SourceKind string was not a
        // known enum member materialize as Unspecified (via the EF value converter's
        // default case). Exclude them so unknown/future source kinds are never
        // swept into auto-complete, preserving forward-compatibility.
        return rows.Where(t => t.SourceKind != UserTaskSourceKind.Unspecified).ToList();
    }

    /// <inheritdoc />
    public Task<int> GetPendingCountAsync(UserTaskType? taskType = null, CancellationToken ct = default)
        => GetPendingCountAsync(taskType, includeMaintenance: true, ct);

    /// <inheritdoc />
    public async Task<int> GetPendingCountAsync(UserTaskType? taskType, bool includeMaintenance, CancellationToken ct = default)
    {
        IQueryable<UserTask> query = _db.UserTasks
            .Where(t => t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress);

        if (taskType.HasValue)
        {
            query = query.Where(t => t.TaskType == taskType.Value);
        }

        if (!includeMaintenance)
        {
            // Fix 8/B: the count must match the filtered list a non-admin can see.
            query = query.Where(t => t.SourceKind != UserTaskSourceKind.Maintenance);
        }

        return await query.CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>> GetSuppressedSourceKeysAsync(
        DateTime updatedAfterUtc, CancellationToken ct = default)
    {
        // Fix F: user-initiated Skipped/Dismissed compiler tasks suppress re-creation
        // until the window lapses. Completed is intentionally excluded so a genuinely
        // recurring condition (e.g. a printer going idle again) can re-materialize.
        List<UserTask> rows = await _db.UserTasks.AsNoTracking()
            .Where(t =>
                (t.Status == UserTaskStatus.Skipped || t.Status == UserTaskStatus.Dismissed) &&
                t.SourceId != null &&
                t.SourceKind != UserTaskSourceKind.Unspecified &&
                t.UpdatedAt >= updatedAfterUtc)
            .ToListAsync(ct);

        // Post-materialize guard: rows whose persisted SourceKind string is not a known
        // enum member surface as Unspecified via the value converter — drop them so
        // unknown/future kinds never suppress a real source key.
        return rows
            .Where(t => t.SourceKind != UserTaskSourceKind.Unspecified && !string.IsNullOrEmpty(t.SourceId))
            .Select(t => (t.SourceKind, t.SourceId!))
            .ToHashSet();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>> GetOpenSuppressedByKeysAsync(
        IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)> activeKeys,
        DateTime? maxAgeUtc = null,
        CancellationToken ct = default)
    {
        HashSet<(UserTaskSourceKind SourceKind, string SourceId)> keySet = activeKeys
            .Where(k => k.SourceKind != UserTaskSourceKind.Unspecified && !string.IsNullOrWhiteSpace(k.SourceId))
            .ToHashSet();
        if (keySet.Count == 0)
        {
            return Array.Empty<(UserTaskSourceKind, string)>();
        }

        // A bounded bootstrap avoids treating ancient terminal rows as current
        // suppression episodes after process restart.
        DateTime effectiveMaxAgeUtc = maxAgeUtc ?? DateTime.UtcNow.AddDays(-30);
        List<UserTask> rows = [];
        foreach ((UserTaskSourceKind SourceKind, string SourceId)[] keyBatch in keySet.Chunk(SuppressedSourceKeyBatchSize))
        {
            Expression<Func<UserTask, bool>> exactPairs = BuildExactSourceKeyPredicate(keyBatch);
            List<UserTask> batchRows = await _db.UserTasks.AsNoTracking()
                .Where(t =>
                    (t.Status == UserTaskStatus.Skipped || t.Status == UserTaskStatus.Dismissed) &&
                    t.SourceId != null &&
                    t.UpdatedAt >= effectiveMaxAgeUtc)
                .Where(exactPairs)
                .ToListAsync(ct);
            rows.AddRange(batchRows);
        }

        return rows
            .Where(t => t.SourceId is not null && keySet.Contains((t.SourceKind, t.SourceId)))
            .Select(t => (t.SourceKind, t.SourceId!))
            .ToHashSet();
    }

    /// <summary>
    /// Creates a provider-translatable disjunction over exact source key pairs, avoiding
    /// the unrelated Cartesian combinations produced by independent IN predicates.
    /// </summary>
    private static Expression<Func<UserTask, bool>> BuildExactSourceKeyPredicate(
        IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)> keys)
    {
        ParameterExpression task = Expression.Parameter(typeof(UserTask), "task");
        Expression predicate = Expression.Constant(false);
        foreach ((UserTaskSourceKind sourceKind, string sourceId) in keys)
        {
            Expression sourceKindMatches = Expression.Equal(
                Expression.Property(task, nameof(UserTask.SourceKind)),
                Expression.Constant(sourceKind));
            Expression sourceIdMatches = Expression.Equal(
                Expression.Property(task, nameof(UserTask.SourceId)),
                Expression.Constant(sourceId));
            predicate = Expression.OrElse(predicate, Expression.AndAlso(sourceKindMatches, sourceIdMatches));
        }

        return Expression.Lambda<Func<UserTask, bool>>(predicate, task);
    }

    /// <summary>
    /// Identifies provider-specific unique-constraint violations without taking direct
    /// dependencies on PostgreSQL or SQL Server provider exception types.
    /// </summary>
    internal static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        Exception? inner = ex.InnerException;
        if (inner is null)
        {
            return false;
        }

        if (inner is Microsoft.Data.Sqlite.SqliteException sqliteEx)
        {
            return sqliteEx.SqliteExtendedErrorCode == 2067;
        }

        Type innerType = inner.GetType();
        if (innerType.FullName == "Npgsql.PostgresException")
        {
            string? sqlState = innerType.GetProperty("SqlState")?.GetValue(inner) as string;
            return sqlState == "23505";
        }

        if (innerType.FullName is "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException")
        {
            object? numberValue = innerType.GetProperty("Number")?.GetValue(inner);
            return numberValue is int number && number is 2601 or 2627;
        }

        string message = inner.Message;
        return message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot insert duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Violation of UNIQUE KEY constraint", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task AddAsync(UserTask task, CancellationToken ct = default)
    {
        _ = _db.UserTasks.Add(task);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public Task TrackAddAsync(UserTask task, CancellationToken ct = default)
    {
        _ = _db.UserTasks.Add(task);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(UserTask task, CancellationToken ct = default)
    {
        task.UpdatedAt = DateTime.UtcNow;
        _ = _db.UserTasks.Update(task);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public Task TrackUpdateAsync(UserTask task, CancellationToken ct = default)
    {
        // Fix D: the compiler passes entities it already tracked via
        // GetOpenCompilerTasksAsync. Calling Update() marks EVERY column modified,
        // so a concurrent user change (e.g. Status -> Completed) committed during
        // the pass would be clobbered on SaveChanges. Rely on the change tracker to
        // persist only the properties the compiler actually mutated. If a caller ever
        // hands us a detached entity, attach + mark modified so it still saves.
        task.UpdatedAt = DateTime.UtcNow;
        if (_db.Entry(task).State == EntityState.Detached)
        {
            _ = _db.UserTasks.Attach(task);
            _db.Entry(task).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateFieldsAsync(UserTask task, IReadOnlyCollection<string> propertyNames, CancellationToken ct = default)
    {
        // Fix R3-5: unlike UpdateAsync/TrackUpdateAsync (which mark the whole entity
        // modified), this only writes the properties the caller names, so a detached
        // caller (loaded via a no-tracking query) cannot clobber columns another
        // writer changed concurrently on the same row.
        task.UpdatedAt = DateTime.UtcNow;
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<UserTask> entry = _db.Entry(task);
        if (entry.State == EntityState.Detached)
        {
            _ = _db.UserTasks.Attach(task);
        }

        entry.Property(nameof(UserTask.UpdatedAt)).IsModified = true;
        foreach (string propertyName in propertyNames)
        {
            entry.Property(propertyName).IsModified = true;
        }

        return _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateFieldsIfOpenAsync(
        UserTask task,
        IReadOnlyCollection<string> propertyNames,
        CancellationToken ct = default)
    {
        HashSet<string> properties = propertyNames.ToHashSet(StringComparer.Ordinal);
        DateTime updatedAt = DateTime.UtcNow;

        if (properties.SetEquals([nameof(UserTask.RelatedEntityIdsJson), nameof(UserTask.Description)]))
        {
            int rows = await _db.UserTasks
                .Where(t => t.Id == task.Id && (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(t => t.UpdatedAt, updatedAt)
                        .SetProperty(t => t.RelatedEntityIdsJson, task.RelatedEntityIdsJson)
                        .SetProperty(t => t.Description, task.Description),
                    ct);

            return rows > 0;
        }

        throw new NotSupportedException(
            $"{nameof(TryUpdateFieldsIfOpenAsync)} does not support the requested property set: {string.Join(", ", properties.OrderBy(p => p, StringComparer.Ordinal))}");
    }

    /// <inheritdoc />
    public async Task<bool> TryAutoCompleteAsync(Guid taskId, DateTime completedAtUtc, CancellationToken ct = default)
    {
        // Fix R3-5: conditional, immediate write — succeeds only if the row is still
        // Pending/InProgress at the moment of the update. A concurrent Skip/Dismiss
        // that already committed wins the race instead of being silently overwritten
        // by the compiler's batched SaveChangesAsync.
        int rows = await _db.UserTasks
            .Where(t => t.Id == taskId && (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, UserTaskStatus.Completed)
                    .SetProperty(t => t.CompletedAt, completedAtUtc)
                    .SetProperty(t => t.UpdatedAt, completedAtUtc),
                ct);

        return rows > 0;
    }

    /// <inheritdoc />
    public Task DetachTrackedAsync(IEnumerable<UserTask> tasks, CancellationToken ct = default)
    {
        foreach (UserTask task in tasks)
        {
            _db.Entry(task).State = EntityState.Detached;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(UserTask task, CancellationToken ct = default)
    {
        _ = _db.UserTasks.Remove(task);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
