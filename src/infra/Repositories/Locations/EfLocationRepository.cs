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
    /// <param name="ct">Cancellation token.</param>
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
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Location>> GetAllWithInactiveAsync(CancellationToken ct)
    {
        return await _dbContext.Locations
            .OrderBy(l => l.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Finds a location by ID.
    /// </summary>
    /// <param name="id">The location ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Location?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Locations.FindAsync(new object[] { id }, cancellationToken: ct);
    }

    /// <summary>
    /// Finds a location by name (case-insensitive, trimmed).
    /// </summary>
    /// <param name="name">The location name to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Location?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();

        // EF Core cannot translate the StringComparison overload, so materialize
        // the candidates and perform a culture-invariant ordinal comparison on the client.
        List<Location> candidates = await _dbContext.Locations
            .Where(l => l.Name != null)
            .ToListAsync(ct);

        return candidates.FirstOrDefault(l => string.Equals(l.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a location with the given name exists (case-insensitive, trimmed).
    /// </summary>
    /// <param name="name">The location name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();

        // Materialize only the name column to minimize transport, then perform
        // a case-insensitive comparison on the client side.
        List<string> names = await _dbContext.Locations
            .Where(l => l.Name != null)
            .Select(l => l.Name)
            .ToListAsync(ct);

        return names.Any(n => string.Equals(n?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds a new location to the repository.
    /// </summary>
    /// <param name="location">The location to add.</param>
    /// <param name="ct">Cancellation token.</param>
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
    /// <param name="location">The location to update.</param>
    /// <param name="ct">Cancellation token.</param>
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
    /// <param name="location">The location to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RemoveAsync(Location location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);

        _dbContext.Locations.Remove(location);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets all printers assigned to a location.
    /// </summary>
    /// <param name="locationId">The location ID.</param>
    /// <param name="ct">Cancellation token.</param>
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
    /// <param name="locationId">The location ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<int> GetPrinterCountAsync(Guid locationId, CancellationToken ct)
    {
        return await _dbContext.Printers
            .Where(p => p.LocationId == locationId)
            .CountAsync(ct);
    }

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await _dbContext.SaveChangesAsync(ct);
    }
}
