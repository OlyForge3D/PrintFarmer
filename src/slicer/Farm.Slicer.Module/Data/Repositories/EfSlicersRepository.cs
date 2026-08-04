using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ISlicersRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfSlicersRepository(SlicerDbContext db) : ISlicersRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task AddAsync(SlicerService svc, CancellationToken ct)
    {
        _ = await _db.SlicerServices.AddAsync(svc, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct)
    {
        return await _db.SlicerServices.OrderBy(s => s.Name).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<SlicerService?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _db.SlicerServices.FindAsync([id], ct);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(SlicerService svc, CancellationToken ct)
    {
        _ = _db.SlicerServices.Remove(svc);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        _ = await _db.SaveChangesAsync(ct);
    }
}
