using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Repository abstraction for querying and mutating filament profiles from OrcaSlicer.
/// </summary>
public interface IFilamentProfileRepository
{
    Task<FilamentProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FilamentProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default);
    Task<FilamentProfile?> GetByHashAsync(string hash, CancellationToken ct = default);
    Task AddAsync(FilamentProfile profile, CancellationToken ct = default);
    Task UpdateAsync(FilamentProfile profile, CancellationToken ct = default);
    Task DeleteAsync(FilamentProfile profile, CancellationToken ct = default);
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}

/// <summary>
/// Entity Framework implementation of filament profile repository.
/// </summary>
public class EfFilamentProfileRepository(AppDbContext db) : IFilamentProfileRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<FilamentProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.FilamentProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

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

    public async Task<FilamentProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.FilamentProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    public async Task AddAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _db.FilamentProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _db.FilamentProfiles.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(FilamentProfile profile, CancellationToken ct = default)
    {
        _db.FilamentProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default)
    {
        var profiles = await _db.FilamentProfiles
            .Where(p => p.IsSystem && p.SlicerType == engine)
            .ToListAsync(ct);

        _db.FilamentProfiles.RemoveRange(profiles);
        await _db.SaveChangesAsync(ct);
        return profiles.Count;
    }
}
