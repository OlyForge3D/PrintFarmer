using Farm.Web.Shared;
using Farm.Web.Shared.Slicer.Messaging;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Orchestrator service for managing slicer operations and job distribution
/// </summary>
public class SlicerOrchestrator : ISlicerOrchestrator
{
    private readonly ISlicerJobQueue _jobQueue;
    private readonly ISlicerFileStorage _fileStorage;
    private readonly ISlicerProgressNotifier _progressNotifier;
    private readonly ILogger<SlicerOrchestrator> _logger;
    private readonly Dictionary<SlicerEngineType, EngineMetadata> _engineCatalog;

    public SlicerOrchestrator(
        ISlicerJobQueue jobQueue,
        ISlicerFileStorage fileStorage,
        ISlicerProgressNotifier progressNotifier,
        ILogger<SlicerOrchestrator> logger)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _progressNotifier = progressNotifier ?? throw new ArgumentNullException(nameof(progressNotifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _engineCatalog = BuildStaticCatalog();
    }

    public async Task<SlicingJobResponse> SubmitJobAsync(SlicingJobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userIdForLog = request.UserId;
        try
        {
            // Validate the request
            await ValidateRequestAsync(request, cancellationToken);

            // Get or create message envelope for idempotency
            var envelope = request.GetOrCreateEnvelope();

            // Check for existing job (idempotency)
            var existingJob = await _jobQueue.FindExistingJobAsync(envelope.CorrelationId, envelope.Checksum, cancellationToken);
            if (existingJob != null)
            {
                _logger.LogInformation("Found existing job {JobId} for correlation {CorrelationId}, returning existing response",
                    existingJob.Id, envelope.CorrelationId);

                // Return existing job response
                var queueStats = await _jobQueue.GetQueueStatsAsync(request.SlicerEngine, cancellationToken);
                return new SlicingJobResponse
                {
                    JobId = existingJob.Id,
                    Status = existingJob.Status,
                    EstimatedCompletionTime = existingJob.CompletedAt ?? DateTime.UtcNow.Add(queueStats.EstimatedWaitTime ?? TimeSpan.Zero),
                    QueuePosition = existingJob.Status == SlicingJobStatus.Queued ? (int)queueStats.QueuedJobs : 0,
                    SlicerWorkerUrl = new Uri(GetSlicerWorkerUrl(request.SlicerEngine), UriKind.RelativeOrAbsolute)
                };
            }

            // Validate checksum if envelope was provided externally
            if (request.Envelope != null)
            {
                // Create content for checksum validation
                var jobContent = SlicingJobContent.FromRequest(request);
                if (!envelope.ValidateChecksum(jobContent))
                {
                    throw new ArgumentException("Request content does not match envelope checksum", nameof(request));
                }
            }

            // Create new job with envelope
            var job = DistributedSlicingJob.FromRequest(request, envelope);

            // Get file size for tracking
            try
            {
                var fileMetadata = await _fileStorage.GetFileMetadataAsync(request.ModelFileUrl.ToString(), cancellationToken);
                if (fileMetadata != null)
                {
                    job.InputFileSizeBytes = fileMetadata.SizeBytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get file metadata for {ModelFileUrl}", request.ModelFileUrl);
            }

            // Enqueue the job
            await _jobQueue.EnqueueAsync(job, cancellationToken);

            // Get queue stats for estimated completion time
            var queueStatsForNew = await _jobQueue.GetQueueStatsAsync(request.SlicerEngine, cancellationToken);
            var estimatedCompletion = DateTime.UtcNow.Add(queueStatsForNew.EstimatedWaitTime ?? TimeSpan.Zero);

            _logger.LogInformation("Submitted new slicing job {JobId} (correlation {CorrelationId}) for user {UserId} with engine {SlicerEngine}",
                job.Id, envelope.CorrelationId, request.UserId, request.SlicerEngine);

            return new SlicingJobResponse
            {
                JobId = job.Id,
                Status = SlicingJobStatus.Queued,
                EstimatedCompletionTime = estimatedCompletion,
                QueuePosition = (int)queueStatsForNew.QueuedJobs, // Approximate
                SlicerWorkerUrl = new Uri(GetSlicerWorkerUrl(request.SlicerEngine), UriKind.RelativeOrAbsolute)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit slicing job for user {UserId}", userIdForLog);
            throw;
        }
    }

    public async Task<SlicingJobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var job = await _jobQueue.GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                return null;
            }

            var response = new SlicingJobStatusResponse
            {
                JobId = job.Id,
                Status = job.Status,
                Progress = job.Progress,
                CreatedAt = job.CreatedAt,
                StartedAt = job.StartedAt,
                CompletedAt = job.CompletedAt,
                WorkerId = job.WorkerId,
                ErrorMessage = job.ErrorMessage,
                ResultFileUrl = job.ResultFileUrl,
                EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
                EstimatedFilamentUsageGrams = job.EstimatedFilamentUsageGrams,
                LayerCount = job.LayerCount ?? 0,
                RetryCount = job.RetryCount,
                ScheduledAt = job.ScheduledAt
            };

            foreach (var kv in job.Metadata)
            {
                response.Metadata[kv.Key] = kv.Value;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job status for {JobId}", jobId);
            throw;
        }
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var job = await _jobQueue.GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                _logger.LogWarning("Cannot cancel job {JobId} - job not found", jobId);
                return false;
            }

            if (job.Status == SlicingJobStatus.Completed || job.Status == SlicingJobStatus.Error || job.Status == SlicingJobStatus.Cancelled)
            {
                _logger.LogWarning("Cannot cancel job {JobId} - job is already in final state: {Status}", jobId, job.Status);
                return false;
            }

            await _jobQueue.CancelJobAsync(jobId, cancellationToken);

            // Notify about cancellation
            await _progressNotifier.NotifyFailureAsync(job, "Job cancelled by user", cancellationToken);

            _logger.LogInformation("Cancelled slicing job {JobId}", jobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel job {JobId}", jobId);
            throw;
        }
    }

    public async Task<List<SlicerEngineInfo>> GetAvailableEnginesAsync(CancellationToken cancellationToken = default)
    {
        var engineInfos = new List<SlicerEngineInfo>();
        var failures = 0;

        foreach (var kvp in _engineCatalog)
        {
            var meta = kvp.Value;
            try
            {
                var queueStats = await _jobQueue.GetQueueStatsAsync(meta.EngineType, cancellationToken);
                engineInfos.Add(new SlicerEngineInfo
                {
                    Engine = meta.EngineType,
                    Version = meta.Version,
                    IsHealthy = true,
                    ActiveWorkers = queueStats.ActiveWorkers,
                    QueueDepth = queueStats.QueuedJobs,
                    SupportedExtensions = meta.SupportedExtensions,
                    EstimatedWaitTime = queueStats.EstimatedWaitTime
                });
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning(ex, "Queue stats retrieval failed for engine {Engine}", meta.EngineType);
                // Return an unhealthy placeholder so the UI can still show engine availability and degraded status
                engineInfos.Add(new SlicerEngineInfo
                {
                    Engine = meta.EngineType,
                    Version = meta.Version,
                    IsHealthy = false,
                    ActiveWorkers = 0,
                    QueueDepth = 0,
                    SupportedExtensions = meta.SupportedExtensions,
                    EstimatedWaitTime = null
                });
            }
        }

        // If every engine failed, escalate as an error
        if (failures == _engineCatalog.Count)
        {
            var ex = new InvalidOperationException("Failed to retrieve queue stats for all slicer engines");
            _logger.LogError(ex, "All engine queue stats retrievals failed");
            throw ex;
        }

        return [.. engineInfos.OrderBy(e => e.Engine)];
    }

    public async Task<Dictionary<SlicerEngineType, SlicerQueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = new Dictionary<SlicerEngineType, SlicerQueueStats>();

            foreach (var engineType in _engineCatalog.Keys)
            {
                var queueStats = await _jobQueue.GetQueueStatsAsync(engineType, cancellationToken);
                stats[engineType] = queueStats;
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all queue stats");
            throw;
        }
    }

    public async Task<List<SlicingJobStatusResponse>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobs = await _jobQueue.GetUserJobsAsync(userId, limit, cancellationToken);

            var responses = new List<SlicingJobStatusResponse>(jobs.Count);
            foreach (var job in jobs)
            {
                var r = new SlicingJobStatusResponse
                {
                    JobId = job.Id,
                    Status = job.Status,
                    Progress = job.Progress,
                    CreatedAt = job.CreatedAt,
                    StartedAt = job.StartedAt,
                    CompletedAt = job.CompletedAt,
                    WorkerId = job.WorkerId,
                    ErrorMessage = job.ErrorMessage,
                    ResultFileUrl = job.ResultFileUrl,
                    EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
                    EstimatedFilamentUsageGrams = job.EstimatedFilamentUsageGrams,
                    LayerCount = job.LayerCount ?? 0
                };
                foreach (var kv in job.Metadata)
                {
                    r.Metadata[kv.Key] = kv.Value;
                }
                responses.Add(r);
            }
            return responses;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user jobs for {UserId}", userId);
            throw;
        }
    }

    public async Task<SlicerOrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = new SlicerOrchestratorHealth
        {
            IsHealthy = true,
            JobQueueHealthy = true,
            FileStorageHealthy = true
        };

        var engineFailures = 0;

        foreach (var kvp in _engineCatalog)
        {
            var meta = kvp.Value;
            try
            {
                var queueStats = await _jobQueue.GetQueueStatsAsync(meta.EngineType, cancellationToken);
                health.Engines[meta.EngineType] = new SlicerEngineInfo
                {
                    Engine = meta.EngineType,
                    Version = meta.Version,
                    IsHealthy = true,
                    ActiveWorkers = queueStats.ActiveWorkers,
                    QueueDepth = queueStats.QueuedJobs,
                    SupportedExtensions = meta.SupportedExtensions,
                    EstimatedWaitTime = queueStats.EstimatedWaitTime
                };
                health.TotalActiveJobs += queueStats.ActiveWorkers;
                health.TotalQueuedJobs += queueStats.QueuedJobs;
            }
            catch (Exception ex)
            {
                engineFailures++;
                _logger.LogWarning(ex, "Health check failed for engine {Engine}", meta.EngineType);
                health.Engines[meta.EngineType] = new SlicerEngineInfo
                {
                    Engine = meta.EngineType,
                    Version = meta.Version,
                    IsHealthy = false,
                    ActiveWorkers = 0,
                    QueueDepth = 0,
                    SupportedExtensions = meta.SupportedExtensions,
                    EstimatedWaitTime = null
                };
                health.IsHealthy = false; // degraded
            }
        }

        // Job queue broad connectivity test
        try
        {
            await _jobQueue.GetQueueStatsAsync(null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job queue health check failed");
            health.JobQueueHealthy = false;
            health.IsHealthy = false;
        }

        // File storage test
        try
        {
            await _fileStorage.FileExistsAsync("health-check-non-existent-file", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File storage health check failed");
            health.FileStorageHealthy = false;
            health.IsHealthy = false;
        }

        // If every engine failed mark overall unhealthy (already set) – nothing extra needed except logging.
        if (engineFailures == _engineCatalog.Count)
        {
            _logger.LogError("All engines failed health checks");
        }

        return health;
    }

    private async Task ValidateRequestAsync(SlicingJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UserId == Guid.Empty)
        {
            throw new ArgumentException("UserId is required", nameof(request));
        }
        if (request.PrinterId == Guid.Empty)
        {
            throw new ArgumentException("PrinterId is required", nameof(request));
        }
        if (request.ModelFileUrl == null)
        {
            throw new ArgumentException("ModelFileUrl is required", nameof(request));
        }

        // Validate slicer engine is available
        if (!_engineCatalog.TryGetValue(request.SlicerEngine, out var engineMeta))
        {
            throw new ArgumentException($"Slicer engine {request.SlicerEngine} is not available", nameof(request));
        }

        // Validate file exists and is supported
        var fileExists = await _fileStorage.FileExistsAsync(request.ModelFileUrl.ToString(), cancellationToken);
        if (!fileExists)
        {
            throw new FileNotFoundException($"Model file not found: {request.ModelFileUrl}");
        }

        // Check file extension
        var extension = Path.GetExtension(request.ModelFileName ?? request.ModelFileUrl.ToString()).ToLowerInvariant();
        if (!engineMeta.SupportedExtensions.Contains(extension))
        {
            throw new ArgumentException($"File extension {extension} is not supported by {request.SlicerEngine}");
        }

        // Validate file size
        var metadata = await _fileStorage.GetFileMetadataAsync(request.ModelFileUrl.ToString(), cancellationToken);
        if (metadata != null && metadata.SizeBytes > 100_000_000)
        {
            throw new ArgumentException("File size exceeds maximum limit of 100MB");
        }
    }

    private static readonly string[] s_orcaSupportedExtensions = [".stl", ".3mf", ".obj"]; // reuse to avoid repeated allocations
    private static readonly string[] s_prusaSupportedExtensions = [".stl", ".3mf", ".obj"]; // same set currently

    private static Dictionary<SlicerEngineType, EngineMetadata> BuildStaticCatalog() =>
        new()
        {
            [SlicerEngineType.OrcaSlicer] = new EngineMetadata(SlicerEngineType.OrcaSlicer, "1.8.x", s_orcaSupportedExtensions),
            [SlicerEngineType.PrusaSlicer] = new EngineMetadata(SlicerEngineType.PrusaSlicer, "2.8.0", s_prusaSupportedExtensions)
        };

    private sealed record EngineMetadata(SlicerEngineType EngineType, string Version, IReadOnlyList<string> SupportedExtensions);

    private static string GetSlicerWorkerUrl(SlicerEngineType engineType)
    {
        // In a real microservices setup, this would return the actual service URL
        // For now, return a placeholder that indicates the engine type
        return engineType switch
        {
            SlicerEngineType.OrcaSlicer => "http://orcaslicer-service:8080",
            SlicerEngineType.PrusaSlicer => "http://prusaslicer-service:8080",
            SlicerEngineType.SuperSlicer => "http://superslicer-service:8080",
            SlicerEngineType.Cura => "http://cura-service:8080",
            _ => "http://unknown-slicer-service:8080"
        };
    }
}
