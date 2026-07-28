using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
    public async Task<bool> TryAddForActiveLeaseAsync(
        Artifact artifact,
        Guid workerId,
        Guid claimToken,
        CancellationToken ct = default)
    {
        await using SlicerDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        DateTime now = DateTime.UtcNow;
        int fenced = await db.SliceJobs
            .Where(job =>
                job.Id == artifact.JobId &&
                job.WorkerId == workerId &&
                job.ClaimToken == claimToken &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt != null &&
                job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(job => job.UpdatedAt, now),
                ct);
        if (fenced != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        artifact.ClaimToken = claimToken;
        _ = db.Artifacts.Add(artifact);
        _ = await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
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
    public async Task<bool> TryReserveForCleanupAsync(
        Guid artifactId,
        Guid? expectedReservationToken,
        Guid reservationToken,
        DateTime reservedAtUtc,
        CancellationToken ct = default)
    {
        await using SlicerDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        int affected = await db.Set<Artifact>()
            .Where(artifact =>
                artifact.Id == artifactId &&
                artifact.CleanupReservationToken == expectedReservationToken &&
                (artifact.PromotionStartedAtUtc == null || artifact.PromotedAtUtc != null))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(artifact => artifact.CleanupReservationToken, reservationToken)
                    .SetProperty(artifact => artifact.CleanupReservedAtUtc, reservedAtUtc),
                ct);
        return affected == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteReservedAsync(
        Guid artifactId,
        Guid reservationToken,
        CancellationToken ct = default)
    {
        await using SlicerDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        int affected = await db.Set<Artifact>()
            .Where(artifact =>
                artifact.Id == artifactId &&
                artifact.CleanupReservationToken == reservationToken)
            .ExecuteDeleteAsync(ct);
        return affected == 1;
    }

    /// <inheritdoc/>
    public async Task ReleaseCleanupReservationAsync(
        Guid artifactId,
        Guid reservationToken,
        CancellationToken ct = default)
    {
        await using SlicerDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        _ = await db.Set<Artifact>()
            .Where(artifact =>
                artifact.Id == artifactId &&
                artifact.CleanupReservationToken == reservationToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(artifact => artifact.CleanupReservationToken, (Guid?)null)
                    .SetProperty(artifact => artifact.CleanupReservedAtUtc, (DateTime?)null),
                ct);
    }

    /// <inheritdoc/>
    public async Task<long> GetTotalSizeAsync(CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        return await db.Set<Artifact>().AsNoTracking().SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
    }

    /// <inheritdoc/>
    public async Task<bool> TryPinForPromotionAsync(
        Guid artifactId,
        Guid? checkpointId,
        PromotionOperationIdentity operation,
        DateTime startedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.OperationId);

        await using SlicerDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        int affected = await db.Set<Artifact>()
            .Where(artifact =>
                artifact.Id == artifactId &&
                artifact.CleanupReservationToken == null &&
                (artifact.PromotionOperationKey == null ||
                    artifact.PromotionOperationKey == operation.Key))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(artifact => artifact.PromotionOperationKey, operation.Key)
                    .SetProperty(artifact => artifact.PromotionOperationId, operation.OperationId)
                    .SetProperty(
                        artifact => artifact.PromotionCheckpointId,
                        artifact => checkpointId ?? artifact.PromotionCheckpointId)
                    .SetProperty(
                        artifact => artifact.PromotionStartedAtUtc,
                        artifact => artifact.PromotionStartedAtUtc ?? startedAtUtc),
                ct);
        return affected == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkPromotedAsync(
        Guid artifactId,
        Guid gcodeFileId,
        DateTime promotedAtUtc,
        CancellationToken ct = default)
    {
        using SlicerDbContext db = _dbFactory.CreateDbContext();
        Artifact? artifact = await db.Set<Artifact>().FirstOrDefaultAsync(a => a.Id == artifactId, ct);
        if (artifact is null)
        {
            return false;
        }

        artifact.PromotedGcodeFileId = gcodeFileId;
        artifact.PromotedAtUtc ??= promotedAtUtc;
        _ = await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> ReleasePromotionPinAsync(
        Guid artifactId,
        PromotionOperationIdentity operation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Key);

        using SlicerDbContext db = _dbFactory.CreateDbContext();
        Artifact? artifact = await db.Set<Artifact>().FirstOrDefaultAsync(a => a.Id == artifactId, ct);
        if (artifact is null ||
            !string.Equals(artifact.PromotionOperationKey, operation.Key, StringComparison.Ordinal) ||
            artifact.PromotedAtUtc is not null)
        {
            return false;
        }

        artifact.PromotionOperationKey = null;
        artifact.PromotionOperationId = null;
        artifact.PromotionCheckpointId = null;
        artifact.PromotionStartedAtUtc = null;
        _ = await db.SaveChangesAsync(ct);
        return true;
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
