using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ISliceJobRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfSliceJobRepository(SlicerDbContext db) : ISliceJobRepository
{
    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc/>
    public async Task AddAsync(SliceJob job, CancellationToken ct = default)
    {
        _ = await _db.SliceJobs.AddAsync(job, ct);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.SliceJobs.FindAsync([id], ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        IQueryable<SliceJob> query = _db.SliceJobs
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.QueuedAt);

        if (offset.HasValue)
        {
            query = query.Skip(offset.Value);
        }

        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default)
    {
        IQueryable<SliceJob> query = _db.SliceJobs
            .Where(j => j.Status == status)
            .OrderByDescending(j => j.QueuedAt);

        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SliceJob>> GetJobsByWorkerIdAsync(Guid workerId, CancellationToken ct = default)
    {
        return await _db.SliceJobs
            .Where(j => j.WorkerId == workerId && j.Status == SliceJobStatus.Processing)
            .OrderByDescending(j => j.StartedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default)
    {
        IQueryable<SliceJob> query = _db.SliceJobs
            .Where(j => j.Status == SliceJobStatus.Queued)
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuedAt);

        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task UpdateStatusAsync(Guid jobId, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.Status = status;
        job.UpdatedAt = DateTime.UtcNow;

        // Set CompletedAt for terminal states
        if (status is SliceJobStatus.Completed or SliceJobStatus.Failed or SliceJobStatus.Cancelled)
        {
            job.CompletedAt ??= DateTime.UtcNow;
        }

        if (progressMessage != null)
        {
            job.ProgressMessage = progressMessage;
        }

        if (progressPercent.HasValue)
        {
            job.ProgressPercent = progressPercent.Value;
        }

        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task MarkStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.Status = SliceJobStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        job.WorkerId = workerId;
        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task MarkCompletedAsync(Guid jobId, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.Status = SliceJobStatus.Completed;
        job.CompletedAt = DateTime.UtcNow;
        job.ResultFileUrl = resultFileUrl;
        job.ProgressPercent = 100;
        job.ProgressMessage = "Completed successfully";
        job.EstimatedPrintTimeSeconds = estimatedPrintTimeSeconds;
        job.FilamentUsedGrams = filamentUsedGrams;
        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task MarkCompletedWithArtifactsAsync(Guid jobId, string resultFileUrl, IEnumerable<Guid> artifactIds, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        Guid[] ids = artifactIds?.Distinct().ToArray() ?? [];
        job.Status = SliceJobStatus.Completed;
        job.CompletedAt = DateTime.UtcNow;
        job.ResultFileUrl = resultFileUrl;
        job.ProgressPercent = 100;
        job.ProgressMessage = "Completed successfully";
        job.EstimatedPrintTimeSeconds = estimatedPrintTimeSeconds;
        job.FilamentUsedGrams = filamentUsedGrams;
        job.ArtifactIdsCsv = ids.Length > 0 ? string.Join(',', ids) : null;

        // Aggregate bytes from artifacts table (slicer-internal relationship)
        if (ids.Length > 0)
        {
            long totalBytes = await _db.Artifacts.Where(a => ids.Contains(a.Id)).SumAsync(a => a.SizeBytes, ct);
            job.ArtifactsTotalBytes = totalBytes;
            job.ArtifactsCount = ids.Length;
        }
        else
        {
            job.ArtifactsTotalBytes = 0;
            job.ArtifactsCount = 0;
        }

        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.Status = SliceJobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.ErrorMessage = errorMessage;
        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task UpdateProgressAsync(Guid jobId, int progressPercent, string progressMessage, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.ProgressPercent = progressPercent;
        job.ProgressMessage = progressMessage;
        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<SliceJob?> ClaimNextJobAsync(Guid workerId, string[]? capabilities, int leaseDurationSeconds, CancellationToken ct = default)
    {
        ValidateLeaseDuration(leaseDurationSeconds);
        while (true)
        {
            DateTime now = DateTime.UtcNow;
            DateTime leaseExpiration = now.AddSeconds(leaseDurationSeconds);
            IQueryable<SliceJob> compatible = _db.SliceJobs
                .AsNoTracking()
                .Where(j => j.Status == SliceJobStatus.Queued ||
                           (j.Status == SliceJobStatus.Processing &&
                            j.LeaseExpiresAt != null &&
                            j.LeaseExpiresAt < now))
                .OrderBy(j => j.Priority)
                .ThenBy(j => j.QueuedAt);
            if (capabilities != null && capabilities.Length > 0)
            {
                // Issue #578 dual-engine: push the capability match to the database so
                // a worker for engine version X is never starved by 50 head-of-queue
                // jobs pinned to version Y. Job matches when its capability list is
                // empty/legacy (null/[]) OR when any advertised worker capability tag
                // appears as a quoted JSON string token in RequiredCapabilitiesJson.
                compatible = compatible.Where(j =>
                    j.RequiredCapabilitiesJson == null ||
                    j.RequiredCapabilitiesJson == string.Empty ||
                    j.RequiredCapabilitiesJson == "[]" ||
                    capabilities.Any(cap =>
                        EF.Functions.Like(j.RequiredCapabilitiesJson!, "%\"" + cap + "\"%")));
            }

            Guid? jobId = await compatible
                .Select(job => (Guid?)job.Id)
                .FirstOrDefaultAsync(ct);
            if (jobId is null)
            {
                return null;
            }

            int claimed = await _db.SliceJobs
                .Where(job =>
                    job.Id == jobId.Value &&
                    (job.Status == SliceJobStatus.Queued ||
                     (job.Status == SliceJobStatus.Processing &&
                      job.LeaseExpiresAt != null &&
                      job.LeaseExpiresAt < now)))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.Status, SliceJobStatus.Processing)
                        .SetProperty(job => job.WorkerId, workerId)
                        .SetProperty(job => job.ClaimedAt, now)
                        .SetProperty(job => job.LeaseExpiresAt, leaseExpiration)
                        .SetProperty(job => job.StartedAt, job => job.StartedAt ?? now)
                        .SetProperty(job => job.UpdatedAt, now),
                    ct);
            if (claimed == 0)
            {
                continue;
            }

            return await _db.SliceJobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId.Value, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<SliceJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken ct = default)
    {
        SliceJob? job = await _db.SliceJobs.FirstOrDefaultAsync(
            j => j.CorrelationId == correlationId && (j.Checksum == checksum || j.Checksum == null), ct);

        return job ?? await _db.SliceJobs.FirstOrDefaultAsync(j => j.CorrelationId == correlationId, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken ct = default)
    {
        return await _db.SliceJobs.AnyAsync(
            j => j.CorrelationId == correlationId && (j.Checksum == checksum || j.Checksum == null), ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SliceJob>> GetStuckJobsAsync(int maxAgeSeconds, int? limit = null, CancellationToken ct = default)
    {
        DateTime threshold = DateTime.UtcNow.AddSeconds(-maxAgeSeconds);
        IQueryable<SliceJob> query = _db.SliceJobs
            .Where(j => j.Status == SliceJobStatus.Processing &&
                        ((j.LeaseExpiresAt != null && j.LeaseExpiresAt < DateTime.UtcNow) ||
                         (j.StartedAt != null && j.StartedAt < threshold)))
            .OrderBy(j => j.StartedAt);

        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> RenewLeaseAsync(
        Guid jobId,
        Guid workerId,
        int leaseDurationSeconds,
        CancellationToken ct = default)
    {
        ValidateLeaseDuration(leaseDurationSeconds);
        DateTime now = DateTime.UtcNow;
        DateTime leaseExpiresAt = now.AddSeconds(leaseDurationSeconds);
        int updated = await _db.SliceJobs
            .Where(job =>
                job.Id == jobId &&
                job.WorkerId == workerId &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt != null &&
                job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.LeaseExpiresAt, leaseExpiresAt)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
        return updated == 1;
    }

    private static void ValidateLeaseDuration(int leaseDurationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            leaseDurationSeconds,
            SliceJob.MinimumLeaseDurationSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            leaseDurationSeconds,
            SliceJob.MaximumLeaseDurationSeconds);
    }

    /// <inheritdoc/>
    public async Task IncrementRetryAndRequeueAsync(Guid jobId, int maxRetries, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.RetryCount += 1;
        job.WorkerId = null;
        job.ClaimedAt = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = DateTime.UtcNow;

        if (job.RetryCount > maxRetries)
        {
            job.Status = SliceJobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = $"Job exceeded max retry attempts ({maxRetries}) and was marked Failed.";
        }
        else
        {
            job.Status = SliceJobStatus.Queued;
            job.QueuedAt = DateTime.UtcNow;
        }

        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(string? status = null, CancellationToken ct = default)
    {
        IQueryable<SliceJob> query = _db.SliceJobs;
        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(j => j.Status == status);
        }

        return await query.CountAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SliceJob>> GetPagedAsync(int page, int pageSize, string? status = null, string? sortBy = null, string? sortDir = null, CancellationToken ct = default)
    {
        IQueryable<SliceJob> query = _db.SliceJobs;
        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(j => j.Status == status);
        }

        bool descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        query = sortBy?.ToLowerInvariant() switch
        {
            "completedat" => descending
                ? query.OrderByDescending(j => j.CompletedAt ?? DateTime.MaxValue).ThenByDescending(j => j.CreatedAt)
                : query.OrderBy(j => j.CompletedAt ?? DateTime.MinValue).ThenBy(j => j.CreatedAt),
            _ => descending ? query.OrderByDescending(j => j.CreatedAt) : query.OrderBy(j => j.CreatedAt),
        };

        return await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task RetryJobAsync(Guid jobId, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job is null)
        {
            return;
        }

        job.Status = SliceJobStatus.Queued;
        job.QueuedAt = DateTime.UtcNow;
        job.WorkerId = null;
        job.ClaimedAt = null;
        job.LeaseExpiresAt = null;
        job.ErrorMessage = null;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.ProgressPercent = 0;
        job.ProgressMessage = null;
        job.RetryCount = 0;
        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        _ = await _db.SaveChangesAsync(ct);
    }
}
