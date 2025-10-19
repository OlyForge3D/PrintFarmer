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

            // Query for queued jobs or jobs with expired leases
            IQueryable<SliceJob> query = _db.SliceJobs
                .Where(j => j.Status == SliceJobStatus.Queued || 
                           (j.Status == SliceJobStatus.Processing && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now))
                .OrderBy(j => j.Priority)
                .ThenBy(j => j.QueuedAt);

            // Filter by capabilities if specified
            if (capabilities != null && capabilities.Length > 0)
            {
                // Jobs with null/empty RequiredCapabilitiesJson match any worker
                // Otherwise, check if job capabilities are a subset of worker capabilities
                query = query.Where(j => 
                    string.IsNullOrEmpty(j.RequiredCapabilitiesJson) ||
                    capabilities.Any(cap => j.RequiredCapabilitiesJson!.Contains($"\"{cap}\"", StringComparison.OrdinalIgnoreCase)));
            }

            // Get first available job
            var job = await query.FirstOrDefaultAsync(ct);
            if (job == null)
            {
                return null;
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

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
