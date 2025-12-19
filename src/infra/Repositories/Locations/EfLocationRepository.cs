using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Locations;

/// <summary>
/// Entity Framework Core implementation of ILocationRepository.
/// Provides data access operations for printer locations using EF Core.
/// </summary>
public class EfLocationRepository : ILocationRepository
{
    private readonly AppDbContext _dbContext;

    public EfLocationRepository(AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <summary>
    /// Gets all active locations.
    /// </summary>
    public async Task<List<Location>> GetAllAsync(CancellationToken ct)
    {
        return await _dbContext.Locations
            .Where(l => l.IsActive)
            .OrderBy(l => l.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets all locations including inactive ones.
    /// </summary>
    public async Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct)
    {
        return await _dbContext.Locations
            .OrderBy(l => l.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Finds a location by ID.
    /// </summary>
    public async Task<Location?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Locations.FindAsync(new object[] { id }, cancellationToken: ct);
    }

    /// <summary>
    /// Finds a location by name (case-sensitive).
    /// </summary>
    public async Task<Location?> FindByNameAsync(string name, CancellationToken ct)
    {
        return await _dbContext.Locations
            .FirstOrDefaultAsync(l => l.Name == name, cancellationToken: ct);
    }

    /// <summary>
    /// Checks if a location with the given name exists (case-sensitive).
    /// </summary>
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken ct)
    {
        return await _dbContext.Locations
            .AnyAsync(l => l.Name == name, cancellationToken: ct);
    }

    /// <summary>
    /// Adds a new location to the repository.
    /// </summary>
    public async Task AddAsync(Location location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);

        location.CreatedAt = DateTime.UtcNow;
        location.ModifiedAt = DateTime.UtcNow;

        await _dbContext.Locations.AddAsync(location, ct);
    }

    /// <summary>
    /// Updates an existing location.
    /// </summary>
    public async Task UpdateAsync(Location location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);

        location.ModifiedAt = DateTime.UtcNow;
        _dbContext.Locations.Update(location);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Removes a location from the repository (hard delete).
    /// </summary>
    public async Task RemoveAsync(Location location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);

        _dbContext.Locations.Remove(location);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets all printers assigned to a location.
    /// </summary>
    public async Task<List<Printer>> GetPrintersInLocationAsync(Guid locationId, CancellationToken ct)
    {
        return await _dbContext.Printers
            .Where(p => p.LocationId == locationId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets the count of printers assigned to a location.
    /// </summary>
    public async Task<int> GetPrinterCountAsync(Guid locationId, CancellationToken ct)
    {
        return await _dbContext.Printers
            .Where(p => p.LocationId == locationId)
            .CountAsync(ct);
    }

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await _dbContext.SaveChangesAsync(ct);
    }
}
