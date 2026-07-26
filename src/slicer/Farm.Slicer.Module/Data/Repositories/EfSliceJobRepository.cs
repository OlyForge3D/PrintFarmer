using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ISliceJobRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfSliceJobRepository(SlicerDbContext db) : ISliceJobRepository
{
    /// <summary>Maximum number of claimable rows inspected per claim attempt.</summary>
    private const int ClaimCandidateWindow = 50;

    /// <summary>Lower bound applied to a requested lease so a worker cannot request a zero lease.</summary>
    private const int MinimumLeaseSeconds = 30;

    /// <summary>Upper bound applied to a requested lease so a worker cannot pin a job indefinitely.</summary>
    private const int MaximumLeaseSeconds = 3600;

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
        DateTime now = DateTime.UtcNow;
        DateTime leaseExpiration = now.AddSeconds(Math.Clamp(leaseDurationSeconds, MinimumLeaseSeconds, MaximumLeaseSeconds));

        // Candidate set: queued jobs plus jobs whose lease has lapsed, highest priority first.
        List<SliceJob> candidates = await _db.SliceJobs
            .AsNoTracking()
            .Where(j => j.Status == SliceJobStatus.Queued ||
                       (j.Status == SliceJobStatus.Processing && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuedAt)
            .Take(ClaimCandidateWindow)
            .ToListAsync(ct);

        foreach (SliceJob candidate in candidates)
        {
            if (!MatchesCapabilities(candidate, capabilities))
            {
                continue;
            }

            Guid leaseToken = Guid.NewGuid();

            // Conditional UPDATE: the WHERE clause re-asserts claimability, so concurrent claimers
            // race in the database and exactly one of them observes a single affected row.
            int affected = await _db.SliceJobs
                .Where(j => j.Id == candidate.Id &&
                            (j.Status == SliceJobStatus.Queued ||
                             (j.Status == SliceJobStatus.Processing &&
                              j.LeaseExpiresAt != null &&
                              j.LeaseExpiresAt < now)))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(j => j.Status, SliceJobStatus.Processing)
                        .SetProperty(j => j.WorkerId, workerId)
                        .SetProperty(j => j.ClaimedAt, now)
                        .SetProperty(j => j.LeaseExpiresAt, leaseExpiration)
                        .SetProperty(j => j.LeaseToken, leaseToken)
                        .SetProperty(j => j.LeaseFence, j => j.LeaseFence + 1)
                        .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                        .SetProperty(j => j.UpdatedAt, now),
                    ct);

            if (affected == 0)
            {
                continue;
            }

            _db.ChangeTracker.Clear();
            return await _db.SliceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == candidate.Id, ct);
        }

        return null;
    }

    /// <summary>
    /// Determines whether a claiming worker can run the job.
    /// </summary>
    /// <param name="job">The candidate job.</param>
    /// <param name="capabilities">Capability tags advertised by the claiming worker.</param>
    /// <returns>
    /// <see langword="true"/> when the worker advertises the job's engine and any explicitly
    /// required capability. A worker that does not advertise capabilities is only offered jobs that
    /// require none, so it can never claim work it is unable to mutate afterwards.
    /// </returns>
    private static bool MatchesCapabilities(SliceJob job, string[]? capabilities)
    {
        bool declaresRequirements =
            !string.IsNullOrEmpty(job.RequiredCapabilitiesJson) && job.RequiredCapabilitiesJson != "[]";

        if (capabilities is not { Length: > 0 })
        {
            return !declaresRequirements;
        }

        string engineTag = SlicerEngineNames.ToCapabilityTag(SlicerEngineNames.Resolve(job));
        if (!capabilities.Contains(engineTag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return !declaresRequirements ||
               capabilities.Any(capability =>
                   job.RequiredCapabilitiesJson!.Contains($"\"{capability}\"", StringComparison.OrdinalIgnoreCase));
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
    public async Task<bool> TryRenewLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid leaseToken,
        long leaseFence,
        int leaseDurationSeconds,
        CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        DateTime leaseExpiration = now.AddSeconds(Math.Clamp(leaseDurationSeconds, MinimumLeaseSeconds, MaximumLeaseSeconds));

        // Renewal is only valid while the lease is still active; an expired lease must be re-claimed.
        int affected = await _db.SliceJobs
            .Where(j => j.Id == jobId &&
                        j.WorkerId == workerId &&
                        j.Status == SliceJobStatus.Processing &&
                        j.LeaseToken == leaseToken &&
                        j.LeaseFence == leaseFence &&
                        j.LeaseExpiresAt != null &&
                        j.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(j => j.LeaseExpiresAt, leaseExpiration)
                    .SetProperty(j => j.UpdatedAt, now),
                ct);

        if (affected > 0)
        {
            _db.ChangeTracker.Clear();
        }

        return affected > 0;
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
        job.LeaseToken = null;
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
