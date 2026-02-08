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
    public async Task UpdateAsync(UserTask task, CancellationToken ct = default)
    {
        task.UpdatedAt = DateTime.UtcNow;
        _ = _db.UserTasks.Update(task);
        _ = await _db.SaveChangesAsync(ct);
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
