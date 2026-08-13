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
        Location? parentLocation = await FindByIdAsync(id, ct);
        if (parentLocation is null)
        {
            return [];
        }

        if (IsUnrootedPath(parentLocation.Path))
        {
            // Path has never been materialized for this location (e.g. legacy/imported data
            // that predates path computation). A blanket prefix match against "/" would
            // incorrectly match every active location in the table, so fall back to a
            // ParentId-based traversal for this location only.
            return await GetDescendantsByParentIdAsync(id, ct);
        }

        string prefix = GetSubtreePathPrefix(parentLocation);

        return await _dbContext.Locations
            .Where(l => l.IsActive && l.Path.StartsWith(prefix))
            .ToListAsync(ct);
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

    public async Task<List<Printer>> GetPrintersInSubtreeAsync(Guid locationId, CancellationToken ct)
    {
        Location? location = await FindByIdAsync(locationId, ct);
        if (location is null)
        {
            return [];
        }

        if (IsUnrootedPath(location.Path))
        {
            HashSet<Guid> subtreeIds = await GetSubtreeIdsByParentIdAsync(locationId, ct);
            return await _dbContext.Printers
                .Include(p => p.Location)
                .Where(p => p.LocationId.HasValue && subtreeIds.Contains(p.LocationId.Value))
                .OrderBy(p => p.Name)
                .ToListAsync(ct);
        }

        string prefix = GetSubtreePathPrefix(location);

        // Single set-based query: printers assigned directly to the location, or to any
        // descendant location (matched via the materialized Path prefix), instead of one
        // GetPrintersInLocationAsync call per location in the subtree.
        return await _dbContext.Printers
            .Include(p => p.Location)
            .Where(p => p.LocationId == locationId
                || (p.Location != null && p.Location.IsActive && p.Location.Path.StartsWith(prefix)))
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<int> GetPrinterCountInSubtreeAsync(Guid locationId, CancellationToken ct)
    {
        Location? location = await FindByIdAsync(locationId, ct);
        if (location is null)
        {
            return 0;
        }

        if (IsUnrootedPath(location.Path))
        {
            HashSet<Guid> subtreeIds = await GetSubtreeIdsByParentIdAsync(locationId, ct);
            return await _dbContext.Printers
                .Where(p => p.LocationId.HasValue && subtreeIds.Contains(p.LocationId.Value))
                .CountAsync(ct);
        }

        string prefix = GetSubtreePathPrefix(location);

        // Single aggregate query: total printer count for the location + all descendants,
        // instead of one GetPrinterCountAsync call per descendant.
        return await _dbContext.Printers
            .Where(p => p.LocationId == locationId
                || (p.Location != null && p.Location.IsActive && p.Location.Path.StartsWith(prefix)))
            .CountAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await _dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds the materialized-Path prefix used to match all descendants of the given location.
    /// </summary>
    private static string GetSubtreePathPrefix(Location location)
    {
        return location.Path.EndsWith('/') ? location.Path : location.Path + "/";
    }

    /// <summary>
    /// A location's Path is considered "unrooted" if it is still at the entity default ("/")
    /// or otherwise empty. No real location produced by <c>LocationService</c> ever has this
    /// value (roots are materialized as "/{Name}"), so this only happens for legacy/imported
    /// data that predates path computation. A prefix match against "/" would match every
    /// active location in the table, so callers must fall back to ParentId-based traversal
    /// instead.
    /// </summary>
    private static bool IsUnrootedPath(string path) => string.IsNullOrEmpty(path) || path == "/";

    /// <summary>
    /// ParentId-based BFS fallback used only when a location's Path has not been materialized.
    /// </summary>
    private async Task<List<Location>> GetDescendantsByParentIdAsync(Guid id, CancellationToken ct)
    {
        var result = new List<Location>();
        var queue = new Queue<Guid>();
        queue.Enqueue(id);

        while (queue.Count > 0)
        {
            Guid currentId = queue.Dequeue();
            List<Location> children = await _dbContext.Locations
                .Where(l => l.IsActive && l.ParentId == currentId)
                .ToListAsync(ct);

            foreach (Location child in children)
            {
                result.Add(child);
                queue.Enqueue(child.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the location's own ID plus all descendant IDs, via ParentId-based traversal.
    /// Used only when the location's Path has not been materialized.
    /// </summary>
    private async Task<HashSet<Guid>> GetSubtreeIdsByParentIdAsync(Guid id, CancellationToken ct)
    {
        List<Location> descendants = await GetDescendantsByParentIdAsync(id, ct);
        var ids = new HashSet<Guid>(descendants.Select(d => d.Id))
        {
            id
        };
        return ids;
    }
}
