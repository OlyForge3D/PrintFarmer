using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IProcessProfileRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfProcessProfileRepository(SlicerDbContext db) : IProcessProfileRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task<ProcessProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProcessProfile>> GetByUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().Where(p => p.CreatedByUserId == userId || p.IsPublic).OrderBy(p => p.Name).ToListAsync(ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProcessProfile>> GetPublicAsync(CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().Where(p => p.IsPublic).OrderBy(p => p.Name).ToListAsync(ct);

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task<ProcessProfile?> GetDefaultAsync(SlicerType engine, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<ProcessProfile> query = _db.ProcessProfiles.AsNoTracking().Where(p => p.SlicerType == engine && p.IsDefault);
        if (userId.HasValue)
        {
            query = query.Where(p => p.CreatedByUserId == userId || p.CreatedByUserId == null);
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ProcessProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.ProcessProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    /// <inheritdoc/>
    public async Task AddAsync(ProcessProfile profile, CancellationToken ct = default)
    {
        profile.CreatedAt = DateTime.UtcNow;
        profile.UpdatedAt = profile.CreatedAt;
        _ = await _db.ProcessProfiles.AddAsync(profile, ct);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(ProcessProfile profile, CancellationToken ct = default)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        _ = _db.ProcessProfiles.Update(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(ProcessProfile profile, CancellationToken ct = default)
    {
        _ = _db.ProcessProfiles.Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SetDefaultAsync(ProcessProfile profile, Guid? userId, CancellationToken ct = default)
    {
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
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
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
            _ = await _db.ProcessProfiles.AddAsync(imported, ct);
            _ = await _db.SaveChangesAsync(ct);
            return imported;
        }

        if (existing.IsSystem && !allowSystemOverride)
        {
            return existing;
        }

        existing.Name = imported.Name;
        existing.Description = imported.Description;
        existing.RawJson = imported.RawJson;
        existing.SettingsJson = imported.SettingsJson;
        existing.LayerHeight = imported.LayerHeight;
        existing.InfillPercentage = imported.InfillPercentage;
        existing.PrintSpeed = imported.PrintSpeed;
        existing.EnableSupports = imported.EnableSupports;
        existing.Quality = imported.Quality;
        existing.UpdatedAt = DateTime.UtcNow;
        _ = await _db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProcessProfile>> GetSystemOrcaProfilesAsync(CancellationToken ct = default) =>
        await _db.ProcessProfiles
            .AsNoTracking()
            .Where(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer)
            .OrderBy(p => p.Quality)
            .ThenBy(p => p.LayerHeight)
            .ToListAsync(ct);

    /// <inheritdoc/>
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
        _ = await _db.SaveChangesAsync(ct);
        return profilesToDelete.Count;
    }
}
