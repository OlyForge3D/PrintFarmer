using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Tasks;

/// <summary>
/// Entity Framework implementation of user task repository.
/// </summary>
public class EfUserTaskRepository(AppDbContext db) : IUserTaskRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task<UserTask?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.UserTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTask>> GetPendingTasksAsync(UserTaskType? taskType = null, CancellationToken ct = default)
    {
        IQueryable<UserTask> query = _db.UserTasks.AsNoTracking()
            .Where(t => t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress);

        if (taskType.HasValue)
        {
            query = query.Where(t => t.TaskType == taskType.Value);
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
    public async Task<int> GetPendingCountAsync(UserTaskType? taskType = null, CancellationToken ct = default)
    {
        IQueryable<UserTask> query = _db.UserTasks
            .Where(t => t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress);

        if (taskType.HasValue)
        {
            query = query.Where(t => t.TaskType == taskType.Value);
        }

        return await query.CountAsync(ct);
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
        task.UpdatedAt = DateTime.UtcNow;
        _ = _db.UserTasks.Update(task);
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
