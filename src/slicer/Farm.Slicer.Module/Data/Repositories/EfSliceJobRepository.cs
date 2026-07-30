using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
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
    public async Task<SliceJob?> GetByActiveWorkerLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        return await _db.SliceJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                job =>
                    job.Id == jobId &&
                    job.WorkerId == workerId &&
                    job.ClaimToken == claimToken &&
                    job.Status == SliceJobStatus.Processing &&
                    job.LeaseExpiresAt != null &&
                    job.LeaseExpiresAt > now,
                ct);
    }

    /// <inheritdoc/>
    public async Task<bool> TryUpdateProgressForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        int progressPercent,
        string progressMessage,
        CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        int updated = await _db.SliceJobs
            .Where(job =>
                job.Id == jobId &&
                job.WorkerId == workerId &&
                job.ClaimToken == claimToken &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt != null &&
                job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.ProgressPercent, progressPercent)
                    .SetProperty(job => job.ProgressMessage, progressMessage)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
        if (updated == 1)
        {
            _db.ChangeTracker.Clear();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<bool> TryCompleteForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string resultFileUrl,
        IEnumerable<Guid> artifactIds,
        int? estimatedPrintTimeSeconds = null,
        decimal? filamentUsedGrams = null,
        CancellationToken ct = default)
    {
        Guid[] ids = artifactIds?.Distinct().ToArray() ?? [];
        int validArtifactCount = ids.Length == 0
            ? 0
            : await _db.Artifacts.CountAsync(
                artifact =>
                    ids.Contains(artifact.Id) &&
                    artifact.JobId == jobId &&
                    artifact.WorkerId == workerId &&
                    artifact.ClaimToken == claimToken,
                ct);
        if (validArtifactCount != ids.Length)
        {
            return false;
        }

        long totalBytes = ids.Length == 0
            ? 0
            : await _db.Artifacts
                .Where(artifact => ids.Contains(artifact.Id))
                .SumAsync(artifact => artifact.SizeBytes, ct);
        string? artifactIdsCsv = ids.Length > 0 ? string.Join(',', ids) : null;
        DateTime now = DateTime.UtcNow;
        int updated = await _db.SliceJobs
            .Where(job =>
                job.Id == jobId &&
                job.WorkerId == workerId &&
                job.ClaimToken == claimToken &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt != null &&
                job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, SliceJobStatus.Completed)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.ResultFileUrl, resultFileUrl)
                    .SetProperty(job => job.ProgressPercent, 100)
                    .SetProperty(job => job.ProgressMessage, "Completed successfully")
                    .SetProperty(job => job.EstimatedPrintTimeSeconds, estimatedPrintTimeSeconds)
                    .SetProperty(job => job.FilamentUsedGrams, filamentUsedGrams)
                    .SetProperty(job => job.ArtifactIdsCsv, artifactIdsCsv)
                    .SetProperty(job => job.ArtifactsTotalBytes, totalBytes)
                    .SetProperty(job => job.ArtifactsCount, ids.Length)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
        if (updated == 1)
        {
            _db.ChangeTracker.Clear();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<bool> TryFailForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string errorMessage,
        CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        int updated = await _db.SliceJobs
            .Where(job =>
                job.Id == jobId &&
                job.WorkerId == workerId &&
                job.ClaimToken == claimToken &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt != null &&
                job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, SliceJobStatus.Failed)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.ErrorMessage, errorMessage)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
        if (updated == 1)
        {
            _db.ChangeTracker.Clear();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<SliceJob?> ClaimNextJobAsync(
        WorkerClaimIdentity worker,
        int leaseDurationSeconds,
        int maxRetries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ValidateLeaseDuration(leaseDurationSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        while (true)
        {
            DateTime now = DateTime.UtcNow;
            DateTime leaseExpiration = now.AddSeconds(leaseDurationSeconds);
            Guid claimToken = Guid.NewGuid();
            string[] capabilities = worker.Capabilities;
            IQueryable<SliceJob> compatible = _db.SliceJobs
                .AsNoTracking()
                .Where(j => j.Status == SliceJobStatus.Queued ||
                           (j.Status == SliceJobStatus.Processing &&
                            j.LeaseExpiresAt != null &&
                            j.LeaseExpiresAt < now))
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.QueuedAt);
            if (capabilities.Length > 0)
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

                bool supportsOrca = capabilities.Contains("orcaslicer", StringComparer.OrdinalIgnoreCase);
                bool supportsPrusa = capabilities.Contains("prusaslicer", StringComparer.OrdinalIgnoreCase);
                bool supportsSuper = capabilities.Contains("superslicer", StringComparer.OrdinalIgnoreCase);
                bool supportsCura = capabilities.Contains("cura", StringComparer.OrdinalIgnoreCase);
                compatible = compatible.Where(job =>
                    (job.SlicerEngineName == null && supportsOrca) ||
                    (job.SlicerEngineName == nameof(SlicerEngineType.OrcaSlicer) && supportsOrca) ||
                    (job.SlicerEngineName == nameof(SlicerEngineType.PrusaSlicer) && supportsPrusa) ||
                    (job.SlicerEngineName == nameof(SlicerEngineType.SuperSlicer) && supportsSuper) ||
                    (job.SlicerEngineName == nameof(SlicerEngineType.Cura) && supportsCura));
            }

            if (worker.IsAttested)
            {
                compatible = compatible.Where(job =>
                    (job.PinnedWorkerId == null &&
                     job.SlicerContainerDigest == null &&
                     job.SlicerBinarySha256 == null) ||
                    (job.PinnedWorkerId == worker.WorkerId &&
                     job.SlicerVersion == worker.Version &&
                     job.SlicerDistribution == worker.Distribution &&
                     job.SlicerContainerDigest == worker.ContainerDigest &&
                     job.SlicerBinarySha256 == worker.BinarySha256));
            }
            else
            {
                compatible = compatible.Where(job =>
                    job.PinnedWorkerId == null &&
                    job.SlicerContainerDigest == null &&
                    job.SlicerBinarySha256 == null);
            }

            var candidate = await compatible
                .Select(job => new
                {
                    job.Id,
                    job.Status,
                    job.RetryCount,
                })
                .FirstOrDefaultAsync(ct);
            if (candidate is null)
            {
                return null;
            }

            if (candidate.Status == SliceJobStatus.Processing &&
                candidate.RetryCount >= maxRetries)
            {
                int failed = await _db.SliceJobs
                    .Where(job =>
                       job.Id == candidate.Id &&
                       job.Status == SliceJobStatus.Processing &&
                       job.RetryCount == candidate.RetryCount &&
                       job.LeaseExpiresAt != null &&
                       job.LeaseExpiresAt < now)
                    .ExecuteUpdateAsync(
                       setters => setters
                           .SetProperty(job => job.Status, SliceJobStatus.Failed)
                           .SetProperty(job => job.RetryCount, maxRetries)
                           .SetProperty(job => job.WorkerId, (Guid?)null)
                           .SetProperty(job => job.ClaimedAt, (DateTime?)null)
                           .SetProperty(job => job.ClaimToken, (Guid?)null)
                           .SetProperty(job => job.LeaseToken, (Guid?)null)
                           .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null)
                           .SetProperty(job => job.CompletedAt, now)
                           .SetProperty(
                               job => job.ErrorMessage,
                               $"Job reached max retry attempts ({maxRetries}) and was marked Failed.")
                           .SetProperty(job => job.UpdatedAt, now),
                       ct);
                if (failed == 0)
                {
                    continue;
                }

                continue;
            }

            IQueryable<SliceJob> claimable = _db.SliceJobs.Where(job => job.Id == candidate.Id);
            if (candidate.Status == SliceJobStatus.Queued)
            {
                claimable = claimable.Where(job => job.Status == SliceJobStatus.Queued);
            }
            else
            {
                claimable = claimable.Where(job =>
                    job.Status == SliceJobStatus.Processing &&
                    job.RetryCount == candidate.RetryCount &&
                    job.RetryCount < maxRetries &&
                    job.LeaseExpiresAt != null &&
                    job.LeaseExpiresAt < now);
            }

            int claimed = await claimable.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, SliceJobStatus.Processing)
                    .SetProperty(job => job.WorkerId, worker.WorkerId)
                    .SetProperty(job => job.ClaimedAt, now)
                    .SetProperty(job => job.ClaimToken, claimToken)
                    .SetProperty(job => job.LeaseToken, claimToken)
                    .SetProperty(job => job.LeaseFence, job => job.LeaseFence + 1)
                    .SetProperty(job => job.LeaseExpiresAt, leaseExpiration)
                    .SetProperty(job => job.StartedAt, job => job.StartedAt ?? now)
                    .SetProperty(
                       job => job.RetryCount,
                       job => candidate.Status == SliceJobStatus.Processing
                           ? job.RetryCount + 1
                           : job.RetryCount)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
            if (claimed == 0)
            {
                continue;
            }

            return await _db.SliceJobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == candidate.Id, ct);
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
    public async Task<IReadOnlyList<SliceJob>> GetExpiredLeaseJobsAsync(
        int? limit = null,
        CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        IQueryable<SliceJob> query = _db.SliceJobs
            .AsNoTracking()
            .Where(j => j.Status == SliceJobStatus.Processing &&
                        j.LeaseExpiresAt != null &&
                        j.LeaseExpiresAt < now)
            .OrderBy(j => j.LeaseExpiresAt);

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
        Guid claimToken,
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
                job.ClaimToken == claimToken &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt != null &&
                job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.LeaseExpiresAt, leaseExpiresAt)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
        if (updated == 1)
        {
            _db.ChangeTracker.Clear();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<bool> TryRecoverExpiredLeaseAsync(
        Guid jobId,
        Guid? expectedWorkerId,
        Guid? expectedClaimToken,
        DateTime expectedLeaseExpiresAt,
        int maxRetries,
        CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        int updated = await _db.SliceJobs
            .Where(job =>
                job.Id == jobId &&
                job.WorkerId == expectedWorkerId &&
                job.ClaimToken == expectedClaimToken &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt == expectedLeaseExpiresAt &&
                job.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        job => job.RetryCount,
                        job => job.RetryCount >= maxRetries
                            ? maxRetries
                            : job.RetryCount + 1)
                    .SetProperty(job => job.WorkerId, (Guid?)null)
                    .SetProperty(job => job.ClaimedAt, (DateTime?)null)
                    .SetProperty(job => job.ClaimToken, (Guid?)null)
                    .SetProperty(job => job.LeaseToken, (Guid?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(
                        job => job.Status,
                        job => job.RetryCount >= maxRetries
                            ? SliceJobStatus.Failed
                            : SliceJobStatus.Queued)
                    .SetProperty(
                        job => job.CompletedAt,
                        job => job.RetryCount >= maxRetries ? now : null)
                    .SetProperty(
                        job => job.ErrorMessage,
                        job => job.RetryCount >= maxRetries
                            ? $"Job reached max retry attempts ({maxRetries}) and was marked Failed."
                            : null)
                    .SetProperty(
                        job => job.QueuedAt,
                        job => job.RetryCount >= maxRetries ? job.QueuedAt : now)
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
    public async Task<bool> TryRenewLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid leaseToken,
        long leaseFence,
        int leaseDurationSeconds,
        CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        DateTime leaseExpiration = now.AddSeconds(Math.Clamp(
            leaseDurationSeconds,
            SliceJob.MinimumLeaseDurationSeconds,
            SliceJob.MaximumLeaseDurationSeconds));

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
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        SliceJob? job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.WorkerId = null;
        job.ClaimedAt = null;
        job.LeaseExpiresAt = null;
        job.LeaseToken = null;
        job.UpdatedAt = DateTime.UtcNow;

        if (job.RetryCount >= maxRetries)
        {
            job.RetryCount = maxRetries;
            job.Status = SliceJobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = $"Job reached max retry attempts ({maxRetries}) and was marked Failed.";
        }
        else
        {
            job.RetryCount += 1;
            job.Status = SliceJobStatus.Queued;
            job.QueuedAt = DateTime.UtcNow;
        }

        await SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> TryRequeueForActiveLeaseAsync(
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        int maxRetries,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        DateTime now = DateTime.UtcNow;
        int updated = await _db.SliceJobs
            .Where(job =>
                job.Id == jobId &&
                job.WorkerId == workerId &&
                job.ClaimToken == claimToken &&
                job.Status == SliceJobStatus.Processing &&
                job.LeaseExpiresAt != null &&
                job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        job => job.RetryCount,
                        job => job.RetryCount >= maxRetries
                            ? maxRetries
                            : job.RetryCount + 1)
                    .SetProperty(job => job.WorkerId, (Guid?)null)
                    .SetProperty(job => job.ClaimedAt, (DateTime?)null)
                    .SetProperty(job => job.ClaimToken, (Guid?)null)
                    .SetProperty(job => job.LeaseToken, (Guid?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(
                        job => job.Status,
                        job => job.RetryCount >= maxRetries
                            ? SliceJobStatus.Failed
                            : SliceJobStatus.Queued)
                    .SetProperty(
                        job => job.CompletedAt,
                        job => job.RetryCount >= maxRetries ? now : null)
                    .SetProperty(
                        job => job.ErrorMessage,
                        job => job.RetryCount >= maxRetries
                            ? $"Job reached max retry attempts ({maxRetries}) and was marked Failed."
                            : null)
                    .SetProperty(
                        job => job.QueuedAt,
                        job => job.RetryCount >= maxRetries ? job.QueuedAt : now)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
        return updated == 1;
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
    public async Task<SliceJob?> TryRetryJobAsync(
        Guid jobId,
        Guid expectedUserId,
        string expectedStatus,
        DateTime expectedUpdatedAt,
        CancellationToken ct = default)
    {
        if (expectedStatus is not SliceJobStatus.Failed and not SliceJobStatus.Cancelled)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        int updated = await _db.SliceJobs
            .Where(job =>
                job.Id == jobId &&
                job.UserId == expectedUserId &&
                job.Status == expectedStatus &&
                job.UpdatedAt == expectedUpdatedAt)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, SliceJobStatus.Queued)
                    .SetProperty(job => job.QueuedAt, now)
                    .SetProperty(job => job.WorkerId, (Guid?)null)
                    .SetProperty(job => job.ClaimedAt, (DateTime?)null)
                    .SetProperty(job => job.ClaimToken, (Guid?)null)
                    .SetProperty(job => job.LeaseToken, (Guid?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(job => job.ErrorMessage, (string?)null)
                    .SetProperty(job => job.StartedAt, (DateTime?)null)
                    .SetProperty(job => job.CompletedAt, (DateTime?)null)
                    .SetProperty(job => job.ProgressPercent, 0)
                    .SetProperty(job => job.ProgressMessage, (string?)null)
                    .SetProperty(job => job.RetryCount, 0)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
        if (updated == 0)
        {
            return null;
        }

        return await _db.SliceJobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId, ct);
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        _ = await _db.SaveChangesAsync(ct);
    }
}
