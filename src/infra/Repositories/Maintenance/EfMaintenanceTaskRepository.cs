using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// EF Core implementation of maintenance task repository.
/// </summary>
public class EfMaintenanceTaskRepository(AppDbContext context) : IMaintenanceTaskRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<MaintenanceTask>> GetAllAsync(string? category = null, bool? activeOnly = null, CancellationToken ct = default)
    {
        IQueryable<MaintenanceTask> query = _context.MaintenanceTasks
            .AsNoTracking()
            .Include(t => t.TaskComponents)
                .ThenInclude(tc => tc.MaintenanceComponent);

        if (category is not null)
        {
            query = query.Where(t => t.Category == category);
        }

        if (activeOnly == true)
        {
            query = query.Where(t => t.IsActive);
        }

        return await query
            .OrderBy(t => t.Category)
            .ThenBy(t => t.TaskName)
            .ToListAsync(ct);
    }

    public async Task<MaintenanceTask?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.MaintenanceTasks
            .Include(t => t.TaskComponents)
                .ThenInclude(tc => tc.MaintenanceComponent)
            .Include(t => t.PlanTasks)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task AddAsync(MaintenanceTask task, CancellationToken ct = default)
    {
        await _context.MaintenanceTasks.AddAsync(task, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(MaintenanceTask task, CancellationToken ct = default)
    {
        _context.MaintenanceTasks.Update(task);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(MaintenanceTask task, CancellationToken ct = default)
    {
        _context.MaintenanceTasks.Remove(task);
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddComponentAsync(MaintenanceTaskComponent taskComponent, CancellationToken ct = default)
    {
        await _context.MaintenanceTaskComponents.AddAsync(taskComponent, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task RemoveComponentAsync(MaintenanceTaskComponent taskComponent, CancellationToken ct = default)
    {
        _context.MaintenanceTaskComponents.Remove(taskComponent);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<List<MaintenanceTaskComponent>> GetTaskComponentsAsync(Guid taskId, CancellationToken ct = default)
    {
        return await _context.MaintenanceTaskComponents
            .AsNoTracking()
            .Where(tc => tc.MaintenanceTaskId == taskId)
            .Include(tc => tc.MaintenanceComponent)
            .ToListAsync(ct);
    }

    public async Task<MaintenanceTaskComponent?> FindTaskComponentAsync(Guid taskId, Guid componentId, CancellationToken ct = default)
    {
        return await _context.MaintenanceTaskComponents
            .FirstOrDefaultAsync(tc => tc.MaintenanceTaskId == taskId && tc.MaintenanceComponentId == componentId, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
