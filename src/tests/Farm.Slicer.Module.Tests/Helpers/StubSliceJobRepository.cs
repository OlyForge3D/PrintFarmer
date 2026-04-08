using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Tests.Helpers;

/// <summary>
/// In-memory stub of <see cref="ISliceJobRepository"/> for unit/integration tests.
/// </summary>
public class StubSliceJobRepository : ISliceJobRepository
{
    public List<SliceJob> Jobs { get; } = new();
    public Task AddAsync(SliceJob job, CancellationToken ct = default) { Jobs.Add(job); return Task.CompletedTask; }
    public Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Jobs.Find(j => j.Id == id));
    public Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default) => Task.FromResult((IReadOnlyList<SliceJob>)Jobs);
    public Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default) => Task.FromResult((IReadOnlyList<SliceJob>)Jobs.FindAll(j => j.Status == status));
    public Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default)
    {
        List<SliceJob> queued = Jobs.FindAll(j => j.Status == SliceJobStatus.Queued);
        if (limit.HasValue)
        {
            queued = queued.GetRange(0, Math.Min(limit.Value, queued.Count));
        }
        return Task.FromResult((IReadOnlyList<SliceJob>)queued);
    }
    public Task<IReadOnlyList<SliceJob>> GetJobsByWorkerIdAsync(Guid workerId, CancellationToken ct = default)
    {
        List<SliceJob> list = Jobs.FindAll(j => j.WorkerId == workerId);
        return Task.FromResult((IReadOnlyList<SliceJob>)list);
    }
    public Task UpdateStatusAsync(Guid id, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default)
    {
        SliceJob? job = Jobs.Find(j => j.Id == id);
        if (job != null) { job.Status = status; }
        return Task.CompletedTask;
    }
    public Task MarkStartedAsync(Guid id, Guid workerId, CancellationToken ct = default)
    {
        SliceJob? job = Jobs.Find(j => j.Id == id);
        if (job != null) { job.Status = SliceJobStatus.Processing; job.WorkerId = workerId; job.StartedAt = DateTime.UtcNow; }
        return Task.CompletedTask;
    }
    public Task MarkCompletedAsync(Guid id, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkCompletedWithArtifactsAsync(Guid jobId, string resultFileUrl, IEnumerable<Guid> artifactIds, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateProgressAsync(Guid jobId, int progressPercent, string progressMessage, CancellationToken ct = default) => Task.CompletedTask;
    public Task<SliceJob?> ClaimNextJobAsync(Guid workerId, string[]? capabilities, int leaseDurationSeconds, CancellationToken ct = default)
    {
        SliceJob? job = Jobs.Find(j => j.Status == SliceJobStatus.Queued);
        if (job != null) { job.Status = SliceJobStatus.Processing; job.WorkerId = workerId; job.ClaimedAt = DateTime.UtcNow; job.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(leaseDurationSeconds); }
        return Task.FromResult(job);
    }
    public Task<IReadOnlyList<SliceJob>> GetStuckJobsAsync(int maxAgeSeconds, int? limit = null, CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        List<SliceJob> stuck = Jobs.FindAll(j => j.Status == SliceJobStatus.Processing && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now);
        if (limit.HasValue) { stuck = stuck.GetRange(0, Math.Min(limit.Value, stuck.Count)); }
        return Task.FromResult((IReadOnlyList<SliceJob>)stuck);
    }
    public Task RenewLeaseAsync(Guid jobId, int leaseDurationSeconds, CancellationToken ct = default)
    {
        SliceJob? j = Jobs.Find(x => x.Id == jobId);
        if (j != null) { j.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(leaseDurationSeconds); }
        return Task.CompletedTask;
    }
    public Task IncrementRetryAndRequeueAsync(Guid jobId, int maxRetries, CancellationToken ct = default)
    {
        SliceJob? j = Jobs.Find(x => x.Id == jobId);
        if (j == null) return Task.CompletedTask;
        j.RetryCount += 1;
        j.WorkerId = null;
        j.ClaimedAt = null;
        j.LeaseExpiresAt = null;
        if (j.RetryCount > maxRetries) { j.Status = SliceJobStatus.Failed; }
        else { j.Status = SliceJobStatus.Queued; j.QueuedAt = DateTime.UtcNow; }
        return Task.CompletedTask;
    }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<SliceJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken ct = default)
    {
        SliceJob? existing = Jobs.Find(j => j.CorrelationId == correlationId && string.Equals(j.Checksum, checksum, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(existing);
    }
    public Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken ct = default)
    {
        bool exists = Jobs.Exists(j => j.CorrelationId == correlationId && string.Equals(j.Checksum, checksum, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(exists);
    }
    public Task<int> CountAsync(string? status = null, CancellationToken ct = default) => Task.FromResult(Jobs.Count(j => status is null || j.Status == status));
    public Task<IReadOnlyList<SliceJob>> GetPagedAsync(int page, int pageSize, string? status = null, string? sortBy = null, string? sortDir = null, CancellationToken ct = default) => Task.FromResult((IReadOnlyList<SliceJob>)Jobs.Where(j => status is null || j.Status == status).Skip((page - 1) * pageSize).Take(pageSize).ToList());
    public Task RetryJobAsync(Guid jobId, CancellationToken ct = default)
    {
        SliceJob? j = Jobs.Find(x => x.Id == jobId);
        if (j is not null) { j.Status = SliceJobStatus.Queued; j.RetryCount += 1; }
        return Task.CompletedTask;
    }
}
