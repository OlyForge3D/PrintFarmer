using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Queue;

public class EfQueueRepository : IQueueRepository
{
    private readonly AppDbContext _db;
    public EfQueueRepository(AppDbContext db) => _db = db;

    public async Task<List<PrintJob>> GetAllAsync(CancellationToken ct) => await _db.PrintJobs.AsNoTracking().ToListAsync(ct);

    public async Task<PrintJob?> FindByIdAsync(Guid id, CancellationToken ct) => await _db.PrintJobs.FindAsync(new object?[] { id }, ct);

    public async Task AddAsync(PrintJob item, CancellationToken ct)
    {
        _ = _db.PrintJobs.Add(item);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(PrintJob item, CancellationToken ct)
    {
        _ = _db.PrintJobs.Remove(item);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);
}
