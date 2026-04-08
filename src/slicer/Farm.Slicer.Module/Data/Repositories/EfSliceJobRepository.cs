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
        DateTime now = DateTime.UtcNow;
        DateTime leaseExpiration = now.AddSeconds(leaseDurationSeconds);

        // Base query: queued or expired lease
        IQueryable<SliceJob> baseQuery = _db.SliceJobs
            .Where(j => j.Status == SliceJobStatus.Queued ||
                       (j.Status == SliceJobStatus.Processing && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now))
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuedAt);

        SliceJob? job;
        if (capabilities != null && capabilities.Length > 0)
        {
            // Materialize a small candidate set then perform capability matching client-side
            List<SliceJob> candidates = await baseQuery.Take(50).ToListAsync(ct);
            job = candidates.FirstOrDefault(j =>
                string.IsNullOrEmpty(j.RequiredCapabilitiesJson) || j.RequiredCapabilitiesJson == "[]" ||
                capabilities.Any(cap => j.RequiredCapabilitiesJson.Contains($"\"{cap}\"", StringComparison.OrdinalIgnoreCase)));
            if (job == null)
            {
                return null;
            }
        }
        else
        {
            job = await baseQuery.FirstOrDefaultAsync(ct);
            if (job == null)
            {
                return null;
            }
        }

        // Atomically claim the job
        job.Status = SliceJobStatus.Processing;
        job.WorkerId = workerId;
        job.ClaimedAt = now;
        job.LeaseExpiresAt = leaseExpiration;
        job.StartedAt ??= now;
        job.UpdatedAt = now;

        await SaveChangesAsync(ct);
        return job;
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
    public async Task RenewLeaseAsync(Guid jobId, int leaseDurationSeconds, CancellationToken ct = default)
    {
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(leaseDurationSeconds);
        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
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
