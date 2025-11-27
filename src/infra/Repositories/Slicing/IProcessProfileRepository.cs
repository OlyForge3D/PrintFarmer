using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Repository abstraction for querying and mutating process profiles.
/// Process profiles contain quality/speed settings only (layer height, infill, speeds, supports).
/// Supports deduplication via RawJson hash and user/system scoping.
/// </summary>
public interface IProcessProfileRepository
{
    Task<ProcessProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessProfile>> GetByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessProfile>> GetPublicAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProcessProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem, Guid? userId = null, CancellationToken ct = default);
    Task<ProcessProfile?> GetDefaultAsync(SlicerType engine, Guid? userId = null, CancellationToken ct = default);
    Task<ProcessProfile?> GetByHashAsync(string hash, CancellationToken ct = default);
    Task AddAsync(ProcessProfile profile, CancellationToken ct = default);
    Task UpdateAsync(ProcessProfile profile, CancellationToken ct = default);
    Task DeleteAsync(ProcessProfile profile, CancellationToken ct = default);
    Task SetDefaultAsync(ProcessProfile profile, Guid? userId, CancellationToken ct = default);
    Task<ProcessProfile> AddOrUpdateFromImportAsync(ProcessProfile imported, bool allowSystemOverride, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessProfile>> GetSystemOrcaProfilesAsync(CancellationToken ct = default);
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}

public class EfProcessProfileRepository(AppDbContext db) : IProcessProfileRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<ProcessProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<ProcessProfile>> GetByUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().Where(p => p.CreatedByUserId == userId || p.IsPublic).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<ProcessProfile>> GetPublicAsync(CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().Where(p => p.IsPublic).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<ProcessProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<ProcessProfile> query = _db.ProcessProfiles.AsNoTracking().Where(p => p.SlicerType == engine);
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

    public async Task<ProcessProfile?> GetDefaultAsync(SlicerType engine, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<ProcessProfile> query = _db.ProcessProfiles.AsNoTracking().Where(p => p.SlicerType == engine && p.IsDefault);
        if (userId.HasValue)
        {
            query = query.Where(p => p.CreatedByUserId == userId || p.CreatedByUserId == null);
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    public async Task<ProcessProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    public async Task AddAsync(ProcessProfile profile, CancellationToken ct = default)
    {
        profile.CreatedAt = DateTime.UtcNow;
        profile.UpdatedAt = profile.CreatedAt;
        await _db.ProcessProfiles.AddAsync(profile, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ProcessProfile profile, CancellationToken ct = default)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        _db.ProcessProfiles.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(ProcessProfile profile, CancellationToken ct = default)
    {
        _db.ProcessProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetDefaultAsync(ProcessProfile profile, Guid? userId, CancellationToken ct = default)
    {
        // Clear existing defaults in same scope (user or global)
        IQueryable<ProcessProfile> scope = _db.ProcessProfiles.Where(p => p.SlicerType == profile.SlicerType && p.Id != profile.Id && p.IsDefault);
        if (userId.HasValue)
        {
            scope = scope.Where(p => p.CreatedByUserId == userId);
        }
        else
        {
            scope = scope.Where(p => p.CreatedByUserId == null);
        }

        foreach (ProcessProfile existing in await scope.ToListAsync(ct))
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
    public async Task<ProcessProfile> AddOrUpdateFromImportAsync(ProcessProfile imported, bool allowSystemOverride, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imported.Hash))
        {
            throw new ArgumentException("Imported profile must have a hash", nameof(imported));
        }

        ProcessProfile? existing = await _db.ProcessProfiles.FirstOrDefaultAsync(p => p.Hash == imported.Hash, ct);
        if (existing == null)
        {
            imported.CreatedAt = DateTime.UtcNow;
            imported.UpdatedAt = imported.CreatedAt;
            await _db.ProcessProfiles.AddAsync(imported, ct);
            await _db.SaveChangesAsync(ct);
            return imported;
        }
        // If existing is system and override not allowed, just return existing
        if (existing.IsSystem && !allowSystemOverride)
        {
            return existing;
        }
        // Update mutable fields
        existing.Name = imported.Name;
        existing.Description = imported.Description;
        existing.RawJson = imported.RawJson;
        existing.MetadataJson = imported.MetadataJson;
        existing.LayerHeight = imported.LayerHeight;
        existing.InfillPercentage = imported.InfillPercentage;
        existing.PrintSpeed = imported.PrintSpeed;
        existing.EnableSupports = imported.EnableSupports;
        existing.Quality = imported.Quality;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// Get all system OrcaSlicer process profiles (ordered by Quality, LayerHeight).
    /// </summary>
    public async Task<IReadOnlyList<ProcessProfile>> GetSystemOrcaProfilesAsync(CancellationToken ct = default) =>
        await _db.ProcessProfiles
            .AsNoTracking()
            .Where(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer)
            .OrderBy(p => p.Quality)
            .ThenBy(p => p.LayerHeight)
            .ToListAsync(ct);

    /// <summary>
    /// Delete all system profiles for a given slicer engine.
    /// Returns the count of profiles deleted.
    /// </summary>
    public async Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default)
    {
        List<ProcessProfile> profilesToDelete = await _db.ProcessProfiles
            .Where(p => p.IsSystem && p.SlicerType == engine)
            .ToListAsync(ct);

        if (profilesToDelete.Count == 0)
        {
            return 0;
        }

        _db.ProcessProfiles.RemoveRange(profilesToDelete);
        await _db.SaveChangesAsync(ct);
        return profilesToDelete.Count;
    }
}
