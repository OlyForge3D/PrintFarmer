using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IMachineProfileRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfMachineProfileRepository(SlicerDbContext db) : IMachineProfileRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task<MachineProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.MachineProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MachineProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<MachineProfile> query = _db.MachineProfiles.AsNoTracking().Where(p => p.SlicerType == engine);

        if (!includeSystem)
        {
            query = query.Where(p => !p.IsSystem);
        }

        if (userId.HasValue)
        {
            query = query.Where(p => p.CreatedByUserId == userId || p.IsSystem);
        }

        return await query.OrderBy(p => p.Manufacturer).ThenBy(p => p.Name).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<MachineProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.MachineProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    /// <inheritdoc/>
    public async Task AddAsync(MachineProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineProfiles.Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(MachineProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineProfiles.Update(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(MachineProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineProfiles.Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default)
    {
        List<MachineProfile> profiles = await _db.MachineProfiles
            .Where(p => p.IsSystem && p.SlicerType == engine)
            .ToListAsync(ct);

        _db.MachineProfiles.RemoveRange(profiles);
        _ = await _db.SaveChangesAsync(ct);
        return profiles.Count;
    }

    /// <inheritdoc/>
    public async Task<bool> HasAnyForPrinterModelAsync(Guid printerModelId, CancellationToken ct = default) =>
        await _db.MachineProfiles.AsNoTracking().AnyAsync(p => p.PrinterModelId == printerModelId, ct);
}
