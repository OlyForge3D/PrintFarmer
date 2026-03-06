using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Locations;

/// <summary>
/// Entity Framework Core implementation of ILocationRepository.
/// Provides data access operations for printer locations using EF Core,
/// including tree/hierarchy queries.
/// </summary>
public class EfLocationRepository : ILocationRepository
{
    private readonly AppDbContext _dbContext;

    public EfLocationRepository(AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<List<Location>> GetAllAsync(CancellationToken ct)
    {
        return await _dbContext.Locations
            .Where(l => l.IsActive)
            .OrderBy(l => l.Path)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync(ct);
    }

    public async Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct)
    {
        return await _dbContext.Locations
            .OrderBy(l => l.Path)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync(ct);
    }

    public async Task<Location?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Locations.FindAsync(new object[] { id }, cancellationToken: ct);
    }

    public async Task<Location?> FindByIdWithChildrenAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Locations
            .Include(l => l.Children.Where(c => c.IsActive))
            .FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<Location?> FindByNameAndParentAsync(string name, Guid? parentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();

        List<Location> candidates = await _dbContext.Locations
            .Where(l => l.ParentId == parentId && l.IsActive)
            .ToListAsync(ct);

        return candidates.FirstOrDefault(l =>
            string.Equals(l.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Location?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();

        List<Location> candidates = await _dbContext.Locations
            .Where(l => l.Name != null)
            .ToListAsync(ct);

        return candidates.FirstOrDefault(l => string.Equals(l.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> ExistsByNameAndParentAsync(string name, Guid? parentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();

        List<string> names = await _dbContext.Locations
            .Where(l => l.ParentId == parentId && l.IsActive && l.Name != null)
            .Select(l => l.Name)
            .ToListAsync(ct);

        return names.Any(n => string.Equals(n?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> ExistsByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();

        List<string> names = await _dbContext.Locations
            .Where(l => l.Name != null)
            .Select(l => l.Name)
            .ToListAsync(ct);

        return names.Any(n => string.Equals(n?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<Location>> GetAllWithChildrenAsync(CancellationToken ct)
    {
        return await _dbContext.Locations
            .Where(l => l.IsActive)
            .Include(l => l.Children.Where(c => c.IsActive))
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync(ct);
    }

    public async Task<List<Location>> GetChildrenAsync(Guid parentId, CancellationToken ct)
    {
        return await _dbContext.Locations
            .Where(l => l.ParentId == parentId && l.IsActive)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync(ct);
    }

    public async Task<List<Location>> GetAncestorsAsync(Guid id, CancellationToken ct)
    {
        var ancestors = new List<Location>();
        Location? current = await FindByIdAsync(id, ct);

        while (current?.ParentId is not null)
        {
            Location? parent = await FindByIdAsync(current.ParentId.Value, ct);
            if (parent is null)
            {
                break;
            }

            ancestors.Insert(0, parent);
            current = parent;
        }

        return ancestors;
    }

    public async Task<List<Location>> GetDescendantsAsync(Guid id, CancellationToken ct)
    {
        var descendants = new List<Location>();
        var queue = new Queue<Guid>();
        queue.Enqueue(id);

        while (queue.Count > 0)
        {
            Guid currentId = queue.Dequeue();
            List<Location> children = await GetChildrenAsync(currentId, ct);

            foreach (Location child in children)
            {
                descendants.Add(child);
                queue.Enqueue(child.Id);
            }
        }

        return descendants;
    }

    public async Task<bool> IsDescendantOfAsync(Guid locationId, Guid potentialAncestorId, CancellationToken ct)
    {
        Location? current = await FindByIdAsync(locationId, ct);

        while (current?.ParentId is not null)
        {
            if (current.ParentId == potentialAncestorId)
            {
                return true;
            }

            current = await FindByIdAsync(current.ParentId.Value, ct);
        }

        return false;
    }

    public async Task AddAsync(Location location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);

        location.CreatedAt = DateTime.UtcNow;
        location.ModifiedAt = DateTime.UtcNow;

        await _dbContext.Locations.AddAsync(location, ct);
    }

    public async Task UpdateAsync(Location location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);

        location.ModifiedAt = DateTime.UtcNow;
        _dbContext.Locations.Update(location);
        await Task.CompletedTask;
    }

    public async Task RemoveAsync(Location location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);

        _dbContext.Locations.Remove(location);
        await Task.CompletedTask;
    }

    public async Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct)
    {
        return await _dbContext.Printers
            .Where(p => p.LocationId == locationId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<int> GetPrinterCountAsync(Guid locationId, CancellationToken ct)
    {
        return await _dbContext.Printers
            .Where(p => p.LocationId == locationId)
            .CountAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await _dbContext.SaveChangesAsync(ct);
    }
}
