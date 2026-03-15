using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Cameras;

/// <summary>
/// Entity Framework Core implementation of ICameraRepository.
/// Provides data access operations for standalone cameras using EF Core.
/// </summary>
public class EfCameraRepository : ICameraRepository
{
    private readonly AppDbContext _dbContext;

    public EfCameraRepository(AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <summary>
    /// Gets all cameras ordered by sort order, then by name.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Camera>> GetAllAsync(CancellationToken ct)
    {
        return await _dbContext.Cameras
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets all enabled cameras ordered by sort order, then by name.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Camera>> GetEnabledAsync(CancellationToken ct)
    {
        return await _dbContext.Cameras
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Finds a camera by ID.
    /// </summary>
    /// <param name="id">The camera ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Camera?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Cameras.FindAsync(new object[] { id }, cancellationToken: ct);
    }

    /// <summary>
    /// Finds a camera by name (case-insensitive, trimmed).
    /// </summary>
    /// <param name="name">The camera name to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Camera?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();

        // EF Core cannot translate the StringComparison overload, so materialize
        // the candidates and perform a culture-invariant ordinal comparison on the client.
        List<Camera> candidates = await _dbContext.Cameras
            .Where(c => c.Name != null)
            .ToListAsync(ct);

        return candidates.FirstOrDefault(c => string.Equals(c.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a camera with the given name exists (case-insensitive, trimmed).
    /// </summary>
    /// <param name="name">The camera name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken ct)
    {
        Camera? existing = await FindByNameAsync(name, ct);
        return existing != null;
    }

    /// <summary>
    /// Adds a new camera to the repository.
    /// </summary>
    /// <param name="camera">The camera to add.</param>
    public void Add(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _dbContext.Cameras.Add(camera);
    }

    /// <summary>
    /// Removes a camera from the repository.
    /// </summary>
    /// <param name="camera">The camera to remove.</param>
    public void Remove(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _dbContext.Cameras.Remove(camera);
    }

    /// <summary>
    /// Gets all cameras attached to a specific printer, ordered by sort order, then by name.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Camera>> GetByPrinterIdAsync(Guid printerId, CancellationToken ct)
    {
        return await _dbContext.Cameras
            .Where(c => c.PrinterId == printerId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Finds a camera attached to a printer with a specific camera type.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="type">The camera type to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Camera?> FindByPrinterIdAndTypeAsync(Guid printerId, CameraType type, CancellationToken ct)
    {
        return await _dbContext.Cameras
            .Where(c => c.PrinterId == printerId && c.CameraType == type)
            .FirstOrDefaultAsync(ct);
    }
}
