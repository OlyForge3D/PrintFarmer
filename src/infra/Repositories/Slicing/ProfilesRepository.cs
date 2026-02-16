using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

public class EfProfilesRepository(AppDbContext db) : IProfilesRepository
{
    private readonly AppDbContext _db = db;

    public async Task<List<ProcessProfile>> GetAllAsync(CancellationToken ct) => await _db.Set<ProcessProfile>().AsNoTracking().ToListAsync(ct);

    public async Task<ProcessProfile?> FindByIdAsync(Guid id, CancellationToken ct) => await _db.Set<ProcessProfile>().FindAsync(new object?[] { id }, ct);

    public async Task AddAsync(ProcessProfile profile, CancellationToken ct)
    {
        _ = _db.Set<ProcessProfile>().Add(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(ProcessProfile profile, CancellationToken ct)
    {
        _ = _db.Set<ProcessProfile>().Remove(profile);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);
}
