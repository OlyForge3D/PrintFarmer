using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Entity Framework implementation of machine profile repository.
/// </summary>
public class EfMachineProfileRepository(AppDbContext db) : IMachineProfileRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<MachineProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Set<MachineProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<MachineProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default)
    {
        IQueryable<MachineProfile> query = _db.Set<MachineProfile>().AsNoTracking().Where(p => p.SlicerType == engine);

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

    public async Task<MachineProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.Set<MachineProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    public async Task AddAsync(MachineProfile profile, CancellationToken ct = default)
    {
        _ = _db.Set<MachineProfile>().Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(MachineProfile profile, CancellationToken ct = default)
    {
        _ = _db.Set<MachineProfile>().Update(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(MachineProfile profile, CancellationToken ct = default)
    {
        _ = _db.Set<MachineProfile>().Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default)
    {
        List<MachineProfile> profiles = await _db.Set<MachineProfile>()
            .Where(p => p.IsSystem && p.SlicerType == engine)
            .ToListAsync(ct);

        _db.Set<MachineProfile>().RemoveRange(profiles);
        _ = await _db.SaveChangesAsync(ct);
        return profiles.Count;
    }

    public async Task<bool> HasAnyForPrinterModelAsync(Guid printerModelId, CancellationToken ct = default) =>
        await _db.Set<MachineProfile>().AsNoTracking().AnyAsync(p => p.PrinterModelId == printerModelId, ct);
}
