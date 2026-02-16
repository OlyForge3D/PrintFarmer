using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Slicing;

public class EfSlicersRepository(AppDbContext db) : ISlicersRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task AddAsync(SlicerService svc, CancellationToken ct)
    {
        _ = await _db.Set<SlicerService>().AddAsync(svc, ct);
    }

    public async Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct)
    {
        return await _db.Set<SlicerService>().OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<SlicerService?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _db.Set<SlicerService>().FindAsync(new object[] { id }, ct);
    }

    public Task RemoveAsync(SlicerService svc, CancellationToken ct)
    {
        _ = _db.Set<SlicerService>().Remove(svc);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        _ = await _db.SaveChangesAsync(ct);
    }
}
