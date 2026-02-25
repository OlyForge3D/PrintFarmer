using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// EF Core implementation of maintenance component (parts inventory) repository.
/// </summary>
public class EfMaintenanceComponentRepository(AppDbContext context) : IMaintenanceComponentRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<MaintenanceComponent>> GetAllAsync(string? category = null, CancellationToken ct = default)
    {
        IQueryable<MaintenanceComponent> query = _context.MaintenanceComponents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(c => c.Category == category);
        }

        return await query
            .OrderBy(c => c.Category)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<MaintenanceComponent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.MaintenanceComponents.FindAsync(new object[] { id }, ct);
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        return await _context.MaintenanceComponents
            .AsNoTracking()
            .Select(c => c.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);
    }

    public async Task<List<MaintenanceComponent>> GetLowStockAsync(CancellationToken ct = default)
    {
        return await _context.MaintenanceComponents
            .AsNoTracking()
            .Where(c => c.InStock < c.MinimumStock)
            .OrderBy(c => c.Category)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task AddAsync(MaintenanceComponent component, CancellationToken ct = default)
    {
        await _context.MaintenanceComponents.AddAsync(component, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(MaintenanceComponent component, CancellationToken ct = default)
    {
        _context.MaintenanceComponents.Update(component);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(MaintenanceComponent component, CancellationToken ct = default)
    {
        _context.MaintenanceComponents.Remove(component);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> IsReferencedByTasksAsync(Guid componentId, CancellationToken ct = default)
    {
        return await _context.MaintenanceTaskComponents
            .AnyAsync(tc => tc.MaintenanceComponentId == componentId, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
