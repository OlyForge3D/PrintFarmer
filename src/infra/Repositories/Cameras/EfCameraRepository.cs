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
            .Include(c => c.Printer)
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
            .Include(c => c.Printer)
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
        return await _dbContext.Cameras
            .Include(c => c.Printer)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>
    /// Finds a camera by name (case-insensitive, trimmed).
    /// Uses server-side ToLower() for portable cross-provider comparison.
    /// </summary>
    /// <param name="name">The camera name to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Camera?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim().ToLower();

        return await _dbContext.Cameras
            .Where(c => c.Name != null && c.Name.ToLower().Trim() == trimmed)
            .FirstOrDefaultAsync(ct);
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
            .Include(c => c.Printer)
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
            .Include(c => c.Printer)
            .Where(c => c.PrinterId == printerId && c.CameraType == type)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Gets all enabled cameras with their associated Printer navigation property loaded.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Camera>> GetEnabledWithPrinterAsync(CancellationToken ct)
    {
        return await _dbContext.Cameras
            .Include(c => c.Printer)
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Camera?> FindByPrinterIdAndSourceAsync(Guid printerId, CameraSource source, CancellationToken ct)
    {
        return await _dbContext.Cameras
            .Include(c => c.Printer)
            .Where(c => c.PrinterId == printerId && c.Source == source)
            .FirstOrDefaultAsync(ct);
    }
}
