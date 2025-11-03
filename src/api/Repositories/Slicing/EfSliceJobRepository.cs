using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.Slicing;

/// <summary>
/// EF Core implementation of ISliceJobRepository
/// </summary>
public class EfSliceJobRepository : ISliceJobRepository
{
    private readonly AppDbContext _db;

    public EfSliceJobRepository(AppDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task AddAsync(SliceJob job, CancellationToken ct = default)
    {
        await _db.SliceJobs.AddAsync(job, ct);
    }

    public async Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.SliceJobs.FindAsync(new object[] { id }, ct);
    }

    public async Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        var query = _db.SliceJobs
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.QueuedAt);

        if (offset.HasValue)
        {
            query = (IOrderedQueryable<SliceJob>)query.Skip(offset.Value);
        }

        if (limit.HasValue)
        {
            query = (IOrderedQueryable<SliceJob>)query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default)
    {
        var query = _db.SliceJobs
            .Where(j => j.Status == status)
            .OrderByDescending(j => j.QueuedAt);

        if (limit.HasValue)
        {
            query = (IOrderedQueryable<SliceJob>)query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SliceJob>> GetJobsByWorkerIdAsync(Guid workerId, CancellationToken ct = default)
    {
        return await _db.SliceJobs
            .Where(j => j.WorkerId == workerId && j.Status == SliceJobStatus.Processing)
            .OrderByDescending(j => j.StartedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default)
    {
        var query = _db.SliceJobs
            .Where(j => j.Status == SliceJobStatus.Queued)
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuedAt);

        if (limit.HasValue)
        {
            query = (IOrderedQueryable<SliceJob>)query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    public async Task UpdateStatusAsync(Guid jobId, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
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
    }

    public async Task MarkStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.Status = SliceJobStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        job.WorkerId = workerId;
        job.UpdatedAt = DateTime.UtcNow;
    }

    public async Task MarkCompletedAsync(Guid jobId, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
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
    }

    public async Task MarkCompletedWithArtifactsAsync(Guid jobId, string resultFileUrl, IEnumerable<Guid> artifactIds, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        var ids = artifactIds?.Distinct().ToArray() ?? Array.Empty<Guid>();
        job.Status = SliceJobStatus.Completed;
        job.CompletedAt = DateTime.UtcNow;
        job.ResultFileUrl = resultFileUrl;
        job.ProgressPercent = 100;
        job.ProgressMessage = "Completed successfully";
        job.EstimatedPrintTimeSeconds = estimatedPrintTimeSeconds;
        job.FilamentUsedGrams = filamentUsedGrams;
        job.ArtifactIdsCsv = ids.Length > 0 ? string.Join(',', ids) : null;
        // Aggregate bytes from artifacts table
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
    }

    public async Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.Status = SliceJobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.ErrorMessage = errorMessage;
        job.UpdatedAt = DateTime.UtcNow;
    }

    public async Task UpdateProgressAsync(Guid jobId, int progressPercent, string progressMessage, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.ProgressPercent = progressPercent;
        job.ProgressMessage = progressMessage;
        job.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<SliceJob?> ClaimNextJobAsync(Guid workerId, string[]? capabilities, int leaseDurationSeconds, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var leaseExpiration = now.AddSeconds(leaseDurationSeconds);

        // Base query: queued or expired lease
        IQueryable<SliceJob> baseQuery = _db.SliceJobs
            .Where(j => j.Status == SliceJobStatus.Queued ||
                       (j.Status == SliceJobStatus.Processing && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now))
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.QueuedAt);

        SliceJob? job;
        if (capabilities != null && capabilities.Length > 0)
        {
            // Materialize a small candidate set (limit 50) then perform capability matching client-side
            var candidates = await baseQuery.Take(50).ToListAsync(ct);
            job = candidates.FirstOrDefault(j =>
                string.IsNullOrEmpty(j.RequiredCapabilitiesJson) || j.RequiredCapabilitiesJson == "[]" ||
                capabilities.Any(cap => j.RequiredCapabilitiesJson.Contains($"\"{cap}\"", StringComparison.OrdinalIgnoreCase)));
            if (job == null)
            {
                return null; // no matching job
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
        if (job.StartedAt == null)
        {
            job.StartedAt = now;
        }
        job.UpdatedAt = now;

        await SaveChangesAsync(ct);
        return job;
    }

    public async Task<IReadOnlyList<SliceJob>> GetStuckJobsAsync(int maxAgeSeconds, int? limit = null, CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow.AddSeconds(-maxAgeSeconds);
        var query = _db.SliceJobs
            .Where(j => j.Status == SliceJobStatus.Processing &&
                        ((j.LeaseExpiresAt != null && j.LeaseExpiresAt < DateTime.UtcNow) || (j.StartedAt != null && j.StartedAt < threshold)))
            .OrderBy(j => j.StartedAt);

        if (limit.HasValue)
        {
            query = (IOrderedQueryable<SliceJob>)query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    public async Task RenewLeaseAsync(Guid jobId, int leaseDurationSeconds, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
        if (job == null)
        {
            return;
        }

        job.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(leaseDurationSeconds);
        job.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }

    public async Task IncrementRetryAndRequeueAsync(Guid jobId, int maxRetries, CancellationToken ct = default)
    {
        var job = await GetByIdAsync(jobId, ct);
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
            // bump queuedAt to now so it can be retried fairly
            job.QueuedAt = DateTime.UtcNow;
        }

        await SaveChangesAsync(ct);
    }

    public async Task<SliceJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken ct = default)
    {
        // Try to find a job with matching CorrelationId and checksum if those fields are populated
        var job = await _db.SliceJobs.FirstOrDefaultAsync(j => j.CorrelationId == correlationId && (j.Checksum == checksum || j.Checksum == null), ct);
        if (job != null)
        {
            return job;
        }

        // Fallback: attempt lookup by correlation only
        return await _db.SliceJobs.FirstOrDefaultAsync(j => j.CorrelationId == correlationId, ct);
    }

    public async Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken ct = default)
    {
        return await _db.SliceJobs.AnyAsync(j => j.CorrelationId == correlationId && (j.Checksum == checksum || j.Checksum == null), ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
