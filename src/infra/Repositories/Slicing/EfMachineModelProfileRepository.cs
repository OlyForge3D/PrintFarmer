using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Entity Framework implementation of machine model profile repository.
/// Machine model profiles are base/template profiles from OrcaSlicer's machine_model_list.
/// </summary>
public class EfMachineModelProfileRepository(AppDbContext db) : IMachineModelProfileRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<MachineModelProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<MachineModelProfile?> GetByNameAsync(string name, string manufacturer, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == name && p.Manufacturer == manufacturer, ct);

    public async Task<IReadOnlyList<MachineModelProfile>> GetByEngineAsync(SlicerType engine, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking()
            .Where(p => p.SlicerType == engine)
            .OrderBy(p => p.Manufacturer)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);

    public async Task<MachineModelProfile?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    public async Task<MachineModelProfile?> GetByPrinterModelIdAsync(Guid printerModelId, CancellationToken ct = default) =>
        await _db.MachineModelProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PrinterModelId == printerModelId, ct);

    public async Task AddAsync(MachineModelProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineModelProfiles.Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(MachineModelProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineModelProfiles.Update(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(MachineModelProfile profile, CancellationToken ct = default)
    {
        _ = _db.MachineModelProfiles.Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

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
