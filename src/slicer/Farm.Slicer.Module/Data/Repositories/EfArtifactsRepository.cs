using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IArtifactsRepository"/> backed by <see cref="SlicerDbContext"/>.
/// Uses <see cref="IDbContextFactory{TContext}"/> for thread-safe scoped contexts.
/// </summary>
public class EfArtifactsRepository(IDbContextFactory<SlicerDbContext> dbFactory) : IArtifactsRepository
{
    private readonly IDbContextFactory<SlicerDbContext> _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

    /// <inheritdoc/>
    public async Task<Artifact> AddAsync(Artifact artifact, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        _ = db.Set<Artifact>().Add(artifact);
        _ = await db.SaveChangesAsync(ct);
        return artifact;
    }

    /// <inheritdoc/>
    public async Task<Artifact?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<Artifact>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Artifact>> GetByJobIdAsync(Guid jobId, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<Artifact>()
            .AsNoTracking()
            .Where(a => a.JobId == jobId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Artifact>> GetAllAsync(CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<Artifact>().AsNoTracking().ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Artifact>> GetOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<Artifact>()
            .AsNoTracking()
            .Where(a => a.CreatedAt < cutoffDate)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<long> GetTotalSizeAsync(CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<Artifact>().AsNoTracking().SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(Artifact artifact, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        _ = db.Set<Artifact>().Update(artifact);
        _ = await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        Artifact? artifact = await db.Set<Artifact>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (artifact == null)
        {
            return false;
        }

        _ = db.Set<Artifact>().Remove(artifact);
        _ = await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc/>
    public async Task DeleteMultipleAsync(IEnumerable<Guid> artifactIds, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        List<Guid> idsList = artifactIds.ToList();
        List<Artifact> artifacts = await db.Set<Artifact>()
            .Where(a => idsList.Contains(a.Id))
            .ToListAsync(ct);

        foreach (Artifact artifact in artifacts)
        {
            _ = db.Set<Artifact>().Remove(artifact);
        }

        _ = await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Worker?> GetWorkerByIdAsync(Guid workerId, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<Worker>().FirstOrDefaultAsync(w => w.Id == workerId, ct);
    }

    /// <inheritdoc/>
    public async Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        _ = db.Set<Worker>().Update(worker);
        _ = await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        _ = await db.SaveChangesAsync(ct);
    }
}
