using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IMachineModelProfileRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfMachineModelProfileRepository(SlicerDbContext db) : IMachineModelProfileRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task<MachineModelProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <inheritdoc/>
    public async Task<MachineModelProfile?> GetByNameAsync(string name, string manufacturer, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == name && p.Manufacturer == manufacturer, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MachineModelProfile>> GetByEngineAsync(SlicerType engine, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking()
            .Where(p => p.SlicerType == engine)
            .OrderBy(p => p.Manufacturer)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public async Task<MachineModelProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    /// <inheritdoc/>
    public async Task<MachineModelProfile?> GetByPrinterModelIdAsync(Guid printerModelId, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PrinterModelId == printerModelId, ct);

    /// <inheritdoc/>
    public async Task AddAsync(MachineModelProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineModelProfiles.Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(MachineModelProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineModelProfiles.Update(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(MachineModelProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineModelProfiles.Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default)
    {
        List<MachineModelProfile> profiles = await _db.MachineModelProfiles
            .Where(p => p.IsSystem && p.SlicerType == engine)
            .ToListAsync(ct);

        _db.MachineModelProfiles.RemoveRange(profiles);
        _ = await _db.SaveChangesAsync(ct);
        return profiles.Count;
    }
}
