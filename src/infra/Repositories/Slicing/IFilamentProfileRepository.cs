using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Repository abstraction for querying and mutating filament profiles from OrcaSlicer.
/// </summary>
public interface IFilamentProfileRepository
{
    /// <summary>Gets a filament profile by ID.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets filament profiles for a slicer engine with optional user filtering.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="includeSystem">Whether to include system profiles.</param>
    /// <param name="userId">Optional user ID to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilamentProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default);

    /// <summary>Gets a filament profile by its content hash.</summary>
    /// <param name="hash">The profile content hash.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>Adds a new filament profile.</summary>
    /// <param name="profile">The profile to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Updates an existing filament profile.</summary>
    /// <param name="profile">The profile to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Deletes a filament profile.</summary>
    /// <param name="profile">The profile to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Deletes all system profiles for a slicer engine.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of profiles deleted.</returns>
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}

/// <summary>
/// Entity Framework implementation of filament profile repository.
/// </summary>
public class EfFilamentProfileRepository(AppDbContext db) : IFilamentProfileRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<FilamentProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Set<FilamentProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<FilamentProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<FilamentProfile> query = _db.Set<FilamentProfile>().AsNoTracking().Where(p => p.SlicerType == engine);

        if (!includeSystem)
        {
            query = query.Where(p => !p.IsSystem);
        }

        if (userId.HasValue)
        {
            query = query.Where(p => p.CreatedByUserId == userId || p.IsSystem);
        }

        return await query.OrderBy(p => p.Material).ToListAsync(ct);
    }

    public async Task<FilamentProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.Set<FilamentProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    public async Task AddAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _ = _db.Set<FilamentProfile>().Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _ = _db.Set<FilamentProfile>().Update(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _ = _db.Set<FilamentProfile>().Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default)
    {
        List<FilamentProfile> profiles = await _db.Set<FilamentProfile>()
            .Where(p => p.IsSystem && p.SlicerType == engine)
            .ToListAsync(ct);

        _db.Set<FilamentProfile>().RemoveRange(profiles);
        _ = await _db.SaveChangesAsync(ct);
        return profiles.Count;
    }
}
