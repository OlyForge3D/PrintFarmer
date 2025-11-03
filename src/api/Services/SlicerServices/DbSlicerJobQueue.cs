using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.SlicerServices
{
    /// <summary>
    /// Database-backed implementation of ISlicerJobQueue which delegates to ISliceJobRepository/EfSliceJobRepository
    /// This provides equivalent semantics for the HTTP-based worker claim/renew/complete flow used by HttpJobPollerService.
    /// </summary>
    public class DbSlicerJobQueue : ISlicerJobQueue
    {
        private readonly ISliceJobRepository _repo;
        private readonly IUnifiedLoggingService _logger;

        public DbSlicerJobQueue(ISliceJobRepository repo, IUnifiedLoggingService logger)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            var sj = ToSliceJob(job);
            return _repo.AddAsync(sj, cancellationToken);
        }

        public async Task<DistributedSlicingJob?> DequeueAsync(string workerId, SlicerEngineType? preferredEngine = null, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(workerId, out var wid))
            {
                // WorkerId may be a GUID string in the shared model; try fallback
                wid = Guid.NewGuid();
            }
            var job = await _repo.ClaimNextJobAsync(wid, preferredEngine == null ? null : new[] { preferredEngine.Value.ToString() }, leaseDurationSeconds: 300, ct: cancellationToken);
            if (job == null)
            {
                return null;
            }

            return ToDistributedJob(job);
        }

        public async Task CompleteJobAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            ArgumentNullException.ThrowIfNull(result);

            // Persist completion summary (no artifact IDs available in SlicingResult here)
            string resultUrl = result.ResultFileUrl?.ToString() ?? string.Empty;
            int? estPrint = result.EstimatedPrintTimeSeconds > 0 ? (int?)Convert.ToInt32(result.EstimatedPrintTimeSeconds) : null;
            decimal? filament = result.EstimatedFilamentUsageGrams > 0 ? (decimal?)Convert.ToDecimal(result.EstimatedFilamentUsageGrams) : null;

            await _repo.MarkCompletedAsync(job.Id, resultUrl, estPrint, filament, cancellationToken);
        }

        public Task FailJobAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default)
            => _repo.MarkFailedAsync(jobId, errorMessage, cancellationToken);

        public Task UpdateProgressAsync(Guid jobId, int progress, string? currentStep = null, CancellationToken cancellationToken = default)
            => _repo.UpdateProgressAsync(jobId, progress, currentStep ?? string.Empty, cancellationToken);

        public async Task<DistributedSlicingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            var job = await _repo.GetByIdAsync(jobId, cancellationToken);
            return job == null ? null : ToDistributedJob(job);
        }

        public Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
            => _repo.MarkFailedAsync(jobId, "Cancelled by operator", cancellationToken);

        public async Task<SlicerQueueStats> GetQueueStatsAsync(SlicerEngineType? engine = null, CancellationToken cancellationToken = default)
        {
            var queued = await _repo.GetByStatusAsync(SliceJobStatus.Queued, limit: null, ct: cancellationToken);
            var processing = await _repo.GetByStatusAsync(SliceJobStatus.Processing, limit: null, ct: cancellationToken);
            var completed = await _repo.GetByStatusAsync(SliceJobStatus.Completed, limit: null, ct: cancellationToken);
            var failed = await _repo.GetByStatusAsync(SliceJobStatus.Failed, limit: null, ct: cancellationToken);

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

        public Task RequeueJobAsync(DistributedSlicingJob job, TimeSpan? delay = null, double jitterPercent = 0.0, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(job);
            // Bump retry and set status back to queued via repository
            return _repo.IncrementRetryAndRequeueAsync(job.Id, maxRetries: 3, ct: cancellationToken);
        }

        public async Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default)
        {
            var sj = await _repo.FindExistingJobAsync(correlationId, checksum, cancellationToken);
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

            var dsj = new DistributedSlicingJob
            {
                Id = sj.Id,
                UserId = sj.UserId,
                CreatedAt = sj.QueuedAt,
                Priority = (SlicingJobPriority)sj.Priority,
                Status = Enum.TryParse<SlicingJobStatus>(sj.Status, true, out var st) ? st : SlicingJobStatus.Queued,
                ModelFileUrl = Uri.TryCreate(sj.ModelFileUrl, UriKind.RelativeOrAbsolute, out var u) ? u : new Uri("about:blank", UriKind.RelativeOrAbsolute),
                ModelFileName = sj.ModelFileName,
                EngineType = (SlicerEngineType)sj.SlicerEngine,
                SlicerEngine = ((SlicerEngineType)sj.SlicerEngine).ToString(),
                WorkerId = sj.WorkerId?.ToString(),
                StartedAt = sj.StartedAt,
                CompletedAt = sj.CompletedAt,
                RetryCount = sj.RetryCount
            };

            return dsj;
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
                SlicerEngine = (int)dj.EngineType
            };
        }
    }
}
