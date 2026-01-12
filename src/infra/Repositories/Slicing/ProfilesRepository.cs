using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

public class EfProfilesRepository : IProfilesRepository
{
    private readonly AppDbContext _db;
    public EfProfilesRepository(AppDbContext db) => _db = db;

    public async Task<List<ProcessProfile>> GetAllAsync(CancellationToken ct) => await _db.ProcessProfiles.AsNoTracking().ToListAsync(ct);

    public async Task<ProcessProfile?> FindByIdAsync(Guid id, CancellationToken ct) => await _db.ProcessProfiles.FindAsync(new object?[] { id }, ct);

    public async Task AddAsync(ProcessProfile profile, CancellationToken ct)
    {
        _ = _db.ProcessProfiles.Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(ProcessProfile profile, CancellationToken ct)
    {
        _ = _db.ProcessProfiles.Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);
}
