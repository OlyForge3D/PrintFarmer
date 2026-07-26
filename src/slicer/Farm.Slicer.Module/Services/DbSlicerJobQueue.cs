using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Database-backed implementation of ISlicerJobQueue which delegates to ISliceJobRepository/EfSliceJobRepository
/// This provides equivalent semantics for the HTTP-based worker claim/renew/complete flow used by HttpJobPollerService.
/// </summary>
public class DbSlicerJobQueue(ISliceJobRepository repo) : ISlicerJobQueue
{
    private readonly ISliceJobRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));

    public Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        SliceJob sj = ToSliceJob(job);
        return _repo.AddAsync(sj, cancellationToken);
    }

    public async Task<DistributedSlicingJob?> DequeueAsync(string workerId, SlicerEngineType? preferredEngine = null, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(workerId, out Guid wid))
        {
            throw new ArgumentException("Worker ID must be a valid GUID.", nameof(workerId));
        }

        SliceJob? job = await _repo.ClaimNextJobAsync(wid, preferredEngine == null ? null : new[] { preferredEngine.Value.ToString() }, leaseDurationSeconds: 300, ct: cancellationToken);
        return job == null ? null : ToDistributedJob(job);
    }

    public async Task CompleteJobAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        ArgumentNullException.ThrowIfNull(result);

        // Persist completion summary (no artifact IDs available in SlicingResult here)
        string resultUrl = result.ResultFileUrl?.ToString() ?? string.Empty;
        int? estPrint = result.EstimatedPrintTimeSeconds > 0 ? (int?)Convert.ToInt32(result.EstimatedPrintTimeSeconds) : null;
        decimal? filament = result.EstimatedFilamentUsageGrams > 0 ? (decimal?)Convert.ToDecimal(result.EstimatedFilamentUsageGrams) : null;

        (Guid workerId, Guid claimToken) = GetClaimIdentity(job);
        bool completed = await _repo.TryCompleteForActiveLeaseAsync(
            job.Id,
            workerId,
            claimToken,
            resultUrl,
            [],
            estPrint,
            filament,
            cancellationToken);
        ThrowIfClaimLost(completed, job.Id);
    }

    public async Task FailJobAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default)
    {
        (Guid workerId, Guid claimToken) = GetClaimIdentity(job);
        bool failed = await _repo.TryFailForActiveLeaseAsync(
            job.Id,
            workerId,
            claimToken,
            errorMessage,
            cancellationToken);
        ThrowIfClaimLost(failed, job.Id);
    }

    public async Task UpdateProgressAsync(DistributedSlicingJob job, int progress, string? currentStep = null, CancellationToken cancellationToken = default)
    {
        (Guid workerId, Guid claimToken) = GetClaimIdentity(job);
        bool updated = await _repo.TryUpdateProgressForActiveLeaseAsync(
            job.Id,
            workerId,
            claimToken,
            progress,
            currentStep ?? string.Empty,
            cancellationToken);
        ThrowIfClaimLost(updated, job.Id);
    }

    public async Task<DistributedSlicingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        SliceJob? job = await _repo.GetByIdAsync(jobId, cancellationToken);
        return job == null ? null : ToDistributedJob(job);
    }

    public Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => _repo.MarkFailedAsync(jobId, "Cancelled by operator", cancellationToken);

    public async Task<SlicerQueueStats> GetQueueStatsAsync(SlicerEngineType? engine = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SliceJob> queued = await _repo.GetByStatusAsync(SliceJobStatus.Queued, limit: null, ct: cancellationToken);
        IReadOnlyList<SliceJob> processing = await _repo.GetByStatusAsync(SliceJobStatus.Processing, limit: null, ct: cancellationToken);
        IReadOnlyList<SliceJob> completed = await _repo.GetByStatusAsync(SliceJobStatus.Completed, limit: null, ct: cancellationToken);
        IReadOnlyList<SliceJob> failed = await _repo.GetByStatusAsync(SliceJobStatus.Failed, limit: null, ct: cancellationToken);

        return new SlicerQueueStats
        {
            Engine = engine ?? SlicerEngineType.OrcaSlicer,
            QueuedJobs = queued.Count,
            ProcessingJobs = processing.Count,
            CompletedJobs = completed.Count,
            FailedJobs = failed.Count,
            ActiveWorkers = 0,
            AverageProcessingTimeSeconds = 0,
            LastUpdated = DateTime.UtcNow
        };
    }

    public Task<List<DistributedSlicingJob>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default)
    {
        return _repo.GetByUserIdAsync(userId, limit, ct: cancellationToken)
            .ContinueWith(t => t.Result.Select(ToDistributedJob).ToList(), cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public Task CleanupOldJobsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        // Not implemented here; repository-level cleanup exists elsewhere
        return Task.CompletedTask;
    }

    public Task RequeueFailedJobsAsync(int maxRetryCount = 3, CancellationToken cancellationToken = default)
    {
        return _repo.SaveChangesAsync(cancellationToken);
    }

    public async Task RequeueJobAsync(DistributedSlicingJob job, TimeSpan? delay = null, double jitterPercent = 0.0, CancellationToken cancellationToken = default)
    {
        (Guid workerId, Guid claimToken) = GetClaimIdentity(job);
        bool requeued = await _repo.TryRequeueForActiveLeaseAsync(
            job.Id,
            workerId,
            claimToken,
            maxRetries: 3,
            cancellationToken);
        ThrowIfClaimLost(requeued, job.Id);
    }

    public async Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default)
    {
        SliceJob? sj = await _repo.FindExistingJobAsync(correlationId, checksum, cancellationToken);
        return sj == null ? null : ToDistributedJob(sj);
    }

    public Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default)
        => _repo.JobExistsAsync(correlationId, checksum, cancellationToken);

    // Mapping helpers
    private static DistributedSlicingJob ToDistributedJob(SliceJob sj)
    {
        if (sj == null)
        {
            return null!;
        }

        SlicerEngineType engine = SlicerEngineNames.Resolve(sj);
        DistributedSlicingJob dsj = new DistributedSlicingJob
        {
            Id = sj.Id,
            ClaimToken = sj.ClaimToken ?? Guid.Empty,
            UserId = sj.UserId,
            CreatedAt = sj.QueuedAt,
            Priority = (SlicingJobPriority)sj.Priority,
            Status = Enum.TryParse(sj.Status, true, out SlicingJobStatus st) ? st : SlicingJobStatus.Queued,
            ModelFileUrl = Uri.TryCreate(sj.ModelFileUrl, UriKind.RelativeOrAbsolute, out Uri? u) ? u : new Uri("about:blank", UriKind.RelativeOrAbsolute),
            ModelFileName = sj.ModelFileName,
            EngineType = engine,
            SlicerEngine = engine.ToString(),
            WorkerId = sj.WorkerId?.ToString(),
            StartedAt = sj.StartedAt,
            CompletedAt = sj.CompletedAt,
            RetryCount = sj.RetryCount,

            // The exact resolved profiles must survive the queue hop; dropping them here is what
            // previously starved the worker of any usable slicer configuration.
            NativeProfiles = NativeSlicerProfiles.FromJob(
                sj.MachineProfileJson,
                sj.ProcessProfileJson,
                sj.FilamentProfileJson,
                sj.MachineProfileSha256,
                sj.ProcessProfileSha256,
                sj.FilamentProfileSha256),
        };

        return dsj;
    }

    private static (Guid WorkerId, Guid ClaimToken) GetClaimIdentity(DistributedSlicingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!Guid.TryParse(job.WorkerId, out Guid workerId) || workerId == Guid.Empty)
        {
            throw new InvalidOperationException($"Slicing job {job.Id} does not have a valid worker claim.");
        }

        if (job.ClaimToken == Guid.Empty)
        {
            throw new InvalidOperationException($"Slicing job {job.Id} does not have a claim token.");
        }

        return (workerId, job.ClaimToken);
    }

    private static void ThrowIfClaimLost(bool operationSucceeded, Guid jobId)
    {
        if (!operationSucceeded)
        {
            throw new InvalidOperationException($"The active claim for slicing job {jobId} is no longer valid.");
        }
    }

    private static SliceJob ToSliceJob(DistributedSlicingJob dj)
    {
        return new SliceJob
        {
            Id = dj.Id == Guid.Empty ? Guid.NewGuid() : dj.Id,
            UserId = dj.UserId,
            QueuedAt = dj.CreatedAt == default ? DateTime.UtcNow : dj.CreatedAt,
            Priority = (int)dj.Priority,
            Status = SliceJobStatus.Queued,
            ModelFileUrl = dj.ModelFileUrl?.ToString() ?? string.Empty,
            ModelFileName = dj.ModelFileName,
            SlicerEngine = (int)dj.EngineType,
            SlicerEngineName = dj.EngineType.ToString(),
            CorrelationId = dj.CorrelationId == Guid.Empty ? null : dj.CorrelationId,
            Checksum = string.IsNullOrWhiteSpace(dj.Checksum) ? null : dj.Checksum,
            MachineProfileJson = dj.NativeProfiles?.MachineJson,
            ProcessProfileJson = dj.NativeProfiles?.ProcessJson,
            FilamentProfileJson = dj.NativeProfiles?.FilamentJson,
            MachineProfileSha256 = dj.NativeProfiles?.MachineSha256,
            ProcessProfileSha256 = dj.NativeProfiles?.ProcessSha256,
            FilamentProfileSha256 = dj.NativeProfiles?.FilamentSha256,
        };
    }
}
