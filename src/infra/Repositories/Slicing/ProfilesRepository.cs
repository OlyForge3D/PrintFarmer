using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

public class EfProfilesRepository : IProfilesRepository
{
    private readonly AppDbContext _db;
    public EfProfilesRepository(AppDbContext db) => _db = db;

    public async Task<List<SlicerProfile>> GetAllAsync(CancellationToken ct) => await _db.SlicerProfiles.AsNoTracking().ToListAsync(ct);

    public async Task<SlicerProfile?> FindByIdAsync(Guid id, CancellationToken ct) => await _db.SlicerProfiles.FindAsync(new object?[] { id }, ct);

    public async Task AddAsync(SlicerProfile profile, CancellationToken ct)
    {
        _ = _db.SlicerProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(SlicerProfile profile, CancellationToken ct)
    {
        _ = _db.SlicerProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);
}
