using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.Slicing;

/// <summary>
/// Repository abstraction for querying and mutating slicer profiles.
/// Supports deduplication via RawJson hash and user/system scoping.
/// </summary>
public interface ISlicerProfileRepository
{
    Task<SlicerProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SlicerProfile>> GetByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<SlicerProfile>> GetPublicAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SlicerProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem, Guid? userId = null, CancellationToken ct = default);
    Task<SlicerProfile?> GetDefaultAsync(SlicerType engine, Guid? userId = null, CancellationToken ct = default);
    Task<SlicerProfile?> GetByHashAsync(string hash, CancellationToken ct = default);
    Task AddAsync(SlicerProfile profile, CancellationToken ct = default);
    Task UpdateAsync(SlicerProfile profile, CancellationToken ct = default);
    Task DeleteAsync(SlicerProfile profile, CancellationToken ct = default);
    Task SetDefaultAsync(SlicerProfile profile, Guid? userId, CancellationToken ct = default);
    Task<SlicerProfile> AddOrUpdateFromImportAsync(SlicerProfile imported, bool allowSystemOverride, CancellationToken ct = default);
}

public class EfSlicerProfileRepository(AppDbContext db) : ISlicerProfileRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<SlicerProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.SlicerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<SlicerProfile>> GetByUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.SlicerProfiles.AsNoTracking().Where(p => p.CreatedByUserId == userId || p.IsPublic).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<SlicerProfile>> GetPublicAsync(CancellationToken ct = default) =>
        await _db.SlicerProfiles.AsNoTracking().Where(p => p.IsPublic).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<SlicerProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<SlicerProfile> query = _db.SlicerProfiles.AsNoTracking().Where(p => p.SlicerType == engine);
        if (!includeSystem)
        {
            query = query.Where(p => !p.IsSystem);
        }

        if (userId.HasValue)
        {
            query = query.Where(p => p.CreatedByUserId == userId || p.IsPublic || p.IsSystem);
        }

        return await query.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<SlicerProfile?> GetDefaultAsync(SlicerType engine, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<SlicerProfile> query = _db.SlicerProfiles.AsNoTracking().Where(p => p.SlicerType == engine && p.IsDefault);
        if (userId.HasValue)
        {
            query = query.Where(p => p.CreatedByUserId == userId || p.CreatedByUserId == null);
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    public async Task<SlicerProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.SlicerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    public async Task AddAsync(SlicerProfile profile, CancellationToken ct = default)
    {
        profile.CreatedAt = DateTime.UtcNow;
        profile.UpdatedAt = profile.CreatedAt;
        await _db.SlicerProfiles.AddAsync(profile, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(SlicerProfile profile, CancellationToken ct = default)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        _db.SlicerProfiles.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(SlicerProfile profile, CancellationToken ct = default)
    {
        _db.SlicerProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetDefaultAsync(SlicerProfile profile, Guid? userId, CancellationToken ct = default)
    {
        // Clear existing defaults in same scope (user or global)
        IQueryable<SlicerProfile> scope = _db.SlicerProfiles.Where(p => p.SlicerType == profile.SlicerType && p.Id != profile.Id && p.IsDefault);
        if (userId.HasValue)
        {
            scope = scope.Where(p => p.CreatedByUserId == userId);
        }
        else
        {
            scope = scope.Where(p => p.CreatedByUserId == null);
        }

        foreach (SlicerProfile existing in await scope.ToListAsync(ct))
        {
            existing.IsDefault = false;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        profile.IsDefault = true;
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds a profile if hash not found, otherwise updates existing metadata if permitted.
    /// </summary>
    public async Task<SlicerProfile> AddOrUpdateFromImportAsync(SlicerProfile imported, bool allowSystemOverride, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imported.Hash))
        {
            throw new ArgumentException("Imported profile must have a hash", nameof(imported));
        }

        SlicerProfile? existing = await _db.SlicerProfiles.FirstOrDefaultAsync(p => p.Hash == imported.Hash, ct);
        if (existing == null)
        {
            imported.CreatedAt = DateTime.UtcNow;
            imported.UpdatedAt = imported.CreatedAt;
            await _db.SlicerProfiles.AddAsync(imported, ct);
            await _db.SaveChangesAsync(ct);
            return imported;
        }
        // If existing is system and override not allowed, just return existing
        if (existing.IsSystem && !allowSystemOverride)
        {
            return existing;
        }
        // Update mutable fields (name/description if user owned, metadata)
        existing.Name = imported.Name;
        existing.Description = imported.Description;
        existing.RawJson = imported.RawJson;
        existing.MetadataJson = imported.MetadataJson;
        existing.Material = imported.Material;
        existing.LayerHeight = imported.LayerHeight;
        existing.InfillPercentage = imported.InfillPercentage;
        existing.NozzleTemperature = imported.NozzleTemperature;
        existing.BedTemperature = imported.BedTemperature;
        existing.PrintSpeed = imported.PrintSpeed;
        existing.EnableSupports = imported.EnableSupports;
        existing.Quality = imported.Quality;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return existing;
    }
}
