using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFilamentProfileRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfFilamentProfileRepository(SlicerDbContext db) : IFilamentProfileRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task<FilamentProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.FilamentProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FilamentProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<FilamentProfile> query = _db.FilamentProfiles.AsNoTracking().Where(p => p.SlicerType == engine);

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

    /// <inheritdoc/>
    public async Task<FilamentProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.FilamentProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    /// <inheritdoc/>
    public async Task AddAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _ = _db.FilamentProfiles.Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _ = _db.FilamentProfiles.Update(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _ = _db.FilamentProfiles.Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default)
    {
        List<FilamentProfile> profiles = await _db.FilamentProfiles
            .Where(p => p.IsSystem && p.SlicerType == engine)
            .ToListAsync(ct);

        _db.FilamentProfiles.RemoveRange(profiles);
        _ = await _db.SaveChangesAsync(ct);
        return profiles.Count;
    }
}
