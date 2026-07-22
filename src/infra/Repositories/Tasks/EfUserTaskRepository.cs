using System.Linq.Expressions;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

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
        _ = await SaveTaskChangesAsync(ct);
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
        _ = await SaveTaskChangesAsync(ct);
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
    public async Task UpdateFieldsAsync(
        UserTask task,
        IReadOnlyCollection<string> propertyNames,
        CancellationToken ct = default)
    {
        // Fix R3-5: unlike UpdateAsync/TrackUpdateAsync (which mark the whole entity
        // modified), this only writes the properties the caller names, so a detached
        // caller (loaded via a no-tracking query) cannot clobber columns another
        // writer changed concurrently on the same row.
        task.UpdatedAt = DateTime.UtcNow;
        EntityEntry<UserTask> entry = _db.Entry(task);
        if (entry.State == EntityState.Detached)
        {
            _ = _db.UserTasks.Attach(task);
        }

        entry.Property(nameof(UserTask.UpdatedAt)).IsModified = true;
        foreach (string propertyName in propertyNames)
        {
            entry.Property(propertyName).IsModified = true;
        }

        _ = await SaveTaskChangesAsync(ct);
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateFieldsIfOpenAsync(
        UserTask task,
        IReadOnlyCollection<string> propertyNames,
        CancellationToken ct = default) =>
        TryUpdateFieldsIfOpenAsync(task, propertyNames, expectedUpdatedAt: null, ct);

    /// <inheritdoc />
    public async Task<bool> TryUpdateFieldsIfOpenAsync(
        UserTask task,
        IReadOnlyCollection<string> propertyNames,
        DateTime? expectedUpdatedAt,
        CancellationToken ct = default)
    {
        HashSet<string> properties = propertyNames.ToHashSet(StringComparer.Ordinal);
        DateTime updatedAt = DateTime.UtcNow;

        if (properties.SetEquals([nameof(UserTask.RelatedEntityIdsJson), nameof(UserTask.Description)]))
        {
            IQueryable<UserTask> query = _db.UserTasks
                .Where(t =>
                    t.Id == task.Id
                    && t.LastMutationSequence == task.LastMutationSequence
                    && (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress));
            if (expectedUpdatedAt.HasValue)
            {
                query = query.Where(t => t.UpdatedAt == expectedUpdatedAt.Value);
            }

            long committedSequence = 0;
            bool updated = await ExecuteTaskMutationAsync(
                async sequence =>
                {
                    committedSequence = sequence;
                    return await query.ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(t => t.UpdatedAt, updatedAt)
                            .SetProperty(t => t.LastMutationSequence, sequence)
                            .SetProperty(t => t.RelatedEntityIdsJson, task.RelatedEntityIdsJson)
                            .SetProperty(t => t.Description, task.Description),
                        ct);
                },
                ct);
            if (updated)
            {
                task.UpdatedAt = updatedAt;
                task.LastMutationSequence = committedSequence;
            }

            return updated;
        }

        throw new NotSupportedException(
            $"{nameof(TryUpdateFieldsIfOpenAsync)} does not support the requested property set: {string.Join(", ", properties.OrderBy(p => p, StringComparer.Ordinal))}");
    }

    /// <inheritdoc />
    public async Task<bool> TryAutoCompleteAsync(
        Guid taskId,
        long expectedLastMutationSequence,
        long originWatermark,
        DateTime completedAtUtc,
        CancellationToken ct = default)
    {
        return await ExecuteTaskMutationAsync(
            sequence => _db.UserTasks
                .Where(t =>
                    t.Id == taskId
                    && (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress)
                    && t.LastMutationSequence == expectedLastMutationSequence
                    && t.LastMutationSequence > 0
                    && t.LastMutationSequence <= originWatermark)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(t => t.Status, UserTaskStatus.Completed)
                        .SetProperty(t => t.CompletedAt, completedAtUtc)
                        .SetProperty(t => t.UpdatedAt, completedAtUtc)
                        .SetProperty(t => t.LastMutationSequence, sequence),
                    ct),
            ct);
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
        _ = await SaveTaskChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        _ = await SaveTaskChangesAsync(ct);

    private async Task<int> SaveTaskChangesAsync(CancellationToken ct)
    {
        List<EntityEntry<UserTask>> taskEntries = _db.ChangeTracker
            .Entries<UserTask>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (taskEntries.Count == 0)
        {
            return await _db.SaveChangesAsync(ct);
        }

        IDbContextTransaction? ownedTransaction = await BeginOwnedTransactionAsync(ct);
        IDbContextTransaction? transaction = ownedTransaction ?? _db.Database.CurrentTransaction;
        string? savepoint = null;
        if (ownedTransaction is null && transaction is not null)
        {
            if (!transaction.SupportsSavepoints)
            {
                throw new InvalidOperationException(
                    "Task mutations require savepoint support inside a caller-owned transaction.");
            }

            savepoint = $"user_task_mutation_{Guid.NewGuid():N}";
            await transaction.CreateSavepointAsync(savepoint, ct);
        }

        try
        {
            long sequence = await BumpMutationSequenceAsync(ct);
            foreach (EntityEntry<UserTask> entry in taskEntries.Where(entry => entry.State is not EntityState.Deleted))
            {
                entry.Property(task => task.LastMutationSequence).CurrentValue = sequence;
                if (entry.State is EntityState.Modified)
                {
                    entry.Property(task => task.LastMutationSequence).IsModified = true;
                }
            }

            int rows = await _db.SaveChangesAsync(ct);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(ct);
            }
            else if (transaction is not null && savepoint is not null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, ct);
            }

            return rows;
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None);
            }
            else if (transaction is not null && savepoint is not null)
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    private async Task<bool> ExecuteTaskMutationAsync(
        Func<long, Task<int>> mutateAsync,
        CancellationToken ct)
    {
        IDbContextTransaction? ownedTransaction = await BeginOwnedTransactionAsync(ct);
        IDbContextTransaction? transaction = ownedTransaction ?? _db.Database.CurrentTransaction;
        string? savepoint = null;

        if (ownedTransaction is null && transaction is not null)
        {
            if (!transaction.SupportsSavepoints)
            {
                throw new InvalidOperationException(
                    "Conditional task mutations require savepoint support inside a caller-owned transaction.");
            }

            savepoint = $"user_task_mutation_{Guid.NewGuid():N}";
            await transaction.CreateSavepointAsync(savepoint, ct);
        }

        try
        {
            long sequence = await BumpMutationSequenceAsync(ct);
            int rows = await mutateAsync(sequence);
            if (rows == 0)
            {
                if (ownedTransaction is not null)
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None);
                }
                else if (transaction is not null && savepoint is not null)
                {
                    await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                }

                return false;
            }

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(ct);
            }
            else if (transaction is not null && savepoint is not null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, ct);
            }

            return true;
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None);
            }
            else if (transaction is not null && savepoint is not null)
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    private async Task<long> BumpMutationSequenceAsync(CancellationToken ct)
    {
        int rows = await _db.MutationCounters
            .Where(counter => counter.Id == MutationCounter.GlobalId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(counter => counter.Value, counter => counter.Value + 1),
                ct);
        if (rows != 1)
        {
            throw new InvalidOperationException("The global mutation counter row is missing.");
        }

        return await _db.MutationCounters
            .AsNoTracking()
            .Where(counter => counter.Id == MutationCounter.GlobalId)
            .Select(counter => counter.Value)
            .SingleAsync(ct);
    }

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            return null;
        }

        return await _db.Database.BeginTransactionAsync(ct);
    }
}
