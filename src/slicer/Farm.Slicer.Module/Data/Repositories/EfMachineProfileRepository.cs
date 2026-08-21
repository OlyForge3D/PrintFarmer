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
        try
        {
            _ = await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Same rationale as AddRangeAsync: a failed save leaves the entity tracked as Added, so
            // every later save on this context resubmits and fails on it again. Detaching keeps one
            // rejected row from blocking the rows that follow it in a per-row retry loop (#1779).
            _db.Entry(profile).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<int> AddRangeAsync(IEnumerable<MachineProfile> profiles, CancellationToken ct = default)
    {
        List<MachineProfile> profileList = profiles as List<MachineProfile> ?? profiles.ToList();
        if (profileList.Count == 0)
        {
            return 0;
        }

        _db.MachineProfiles.AddRange(profileList);
        try
        {
            _ = await _db.SaveChangesAsync(ct);
            return profileList.Count;
        }
        catch (DbUpdateException)
        {
            // A failed SaveChangesAsync does not roll back the change tracker: the whole
            // batch would remain tracked as Added, causing any per-row fallback retry to
            // resubmit the entire (still-poisoned) batch instead of isolating the bad row.
            // Detach so the caller can safely retry entities one at a time.
            foreach (MachineProfile profile in profileList)
            {
                _db.Entry(profile).State = EntityState.Detached;
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<HashSet<string>> GetExistingSystemHashesAsync(IEnumerable<string> hashes, SlicerType engine, CancellationToken ct = default)
    {
        List<string> hashList = hashes as List<string> ?? hashes.ToList();
        if (hashList.Count == 0)
        {
            return new HashSet<string>();
        }

        List<string> existing = await _db.MachineProfiles
            .AsNoTracking()
            .Where(p => p.Hash != null && hashList.Contains(p.Hash) && p.IsSystem && p.SlicerType == engine)
            .Select(p => p.Hash!)
            .ToListAsync(ct);

        return new HashSet<string>(existing);
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
