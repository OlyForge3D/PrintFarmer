using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IProfilesRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfProfilesRepository(SlicerDbContext db) : IProfilesRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task<List<ProcessProfile>> GetAllAsync(CancellationToken ct) =>
        await _db.ProcessProfiles.AsNoTracking().ToListAsync(ct);

    /// <inheritdoc/>
    public async Task<ProcessProfile?> FindByIdAsync(Guid id, CancellationToken ct) =>
        await _db.ProcessProfiles.FindAsync([id], ct);

    /// <inheritdoc/>
    public async Task AddAsync(ProcessProfile profile, CancellationToken ct)
    {
        _ = _db.ProcessProfiles.Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(ProcessProfile profile, CancellationToken ct)
    {
        _ = _db.ProcessProfiles.Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct) =>
        await _db.SaveChangesAsync(ct);
}
