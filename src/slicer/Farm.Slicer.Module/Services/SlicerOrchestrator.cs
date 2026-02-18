using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Messaging;
using Farm.Slicer.Module.Models;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Orchestrator service for managing slicer operations and job distribution
/// </summary>
public class SlicerOrchestrator(
    ISlicerJobQueue jobQueue,
    ISlicerFileStorage fileStorage,
    ISlicerProgressNotifier progressNotifier,
    ILogger<SlicerOrchestrator> logger) : ISlicerOrchestrator
{
    private readonly ISlicerJobQueue _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
    private readonly ISlicerFileStorage _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
    private readonly ISlicerProgressNotifier _progressNotifier = progressNotifier ?? throw new ArgumentNullException(nameof(progressNotifier));
    private readonly ILogger<SlicerOrchestrator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Dictionary<SlicerEngineType, EngineMetadata> _engineCatalog = BuildStaticCatalog();

    public async Task<SlicingJobResponse> SubmitJobAsync(SlicingJobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guid userIdForLog = request.UserId;
        try
        {
            // Validate the request
            await ValidateRequestAsync(request, cancellationToken);

            // Get or create message envelope for idempotency
            MessageEnvelope envelope = request.GetOrCreateEnvelope();

            // Check for existing job (idempotency)
            DistributedSlicingJob? existingJob = await _jobQueue.FindExistingJobAsync(envelope.CorrelationId, envelope.Checksum, cancellationToken);
            if (existingJob != null)
            {
                _logger.LogInformation($"Found existing job {existingJob.Id} for correlation {envelope.CorrelationId}, returning existing response");

                // Return existing job response
                SlicerQueueStats infraStats = await _jobQueue.GetQueueStatsAsync((SlicerEngineType)(int)request.SlicerEngine, cancellationToken);
                return new SlicingJobResponse
                {
                    JobId = existingJob.Id,
                    Status = (SlicingJobStatus)(int)existingJob.Status,
                    EstimatedCompletionTime = existingJob.CompletedAt ?? DateTime.UtcNow.Add(infraStats.EstimatedWaitTime ?? TimeSpan.Zero),
                    QueuePosition = existingJob.Status == SlicingJobStatus.Queued ? (int)infraStats.QueuedJobs : 0,
                    SlicerWorkerUrl = new Uri(GetSlicerWorkerUrl(request.SlicerEngine), UriKind.RelativeOrAbsolute)
                };
            }

            // Validate checksum if envelope was provided externally
            if (request.Envelope != null)
            {
                // Create content for checksum validation
                SlicingJobContent jobContent = SlicingJobContent.FromRequest(request);
                if (!envelope.ValidateChecksum(jobContent))
                {
                    throw new ArgumentException("Request content does not match envelope checksum", nameof(request));
                }
            }

            // Create new job with envelope (construct infra type for queue)
            DistributedSlicingJob job = new()
            {
                Id = envelope.JobId,
                UserId = request.UserId,
                PrinterId = request.PrinterId,
                ModelFileUrl = request.ModelFileUrl,
                ModelFileName = request.ModelFileName,
                EngineType = (SlicerEngineType)(int)request.SlicerEngine,
                SlicerEngine = request.SlicerEngine.ToString(),
                Priority = (SlicingJobPriority)(int)request.Priority,
                Status = SlicingJobStatus.Queued,
                CreatedAt = DateTime.UtcNow,
                CorrelationId = envelope.CorrelationId,
                Checksum = envelope.Checksum,
                Attempt = envelope.Attempt,
                SubmittedAt = envelope.SubmittedAt,
                EnvelopeVersion = envelope.Version,
            };

            if (request.Metadata?.Count > 0)
            {
                foreach (KeyValuePair<string, object> kv in request.Metadata)
                {
                    job.Metadata[kv.Key] = kv.Value;
                }
            }

            // Get file size for tracking
            try
            {
                SlicerFileMetadata? fileMetadata = await _fileStorage.GetFileMetadataAsync(request.ModelFileUrl.ToString(), cancellationToken);
                if (fileMetadata != null)
                {
                    job.InputFileSizeBytes = fileMetadata.SizeBytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Could not get file metadata for {request.ModelFileUrl}");
            }

            // Enqueue the job
            await _jobQueue.EnqueueAsync(job, cancellationToken);

            // Get queue stats for estimated completion time
            SlicerQueueStats infraStatsForNew = await _jobQueue.GetQueueStatsAsync((SlicerEngineType)(int)request.SlicerEngine, cancellationToken);
            DateTime estimatedCompletion = DateTime.UtcNow.Add(infraStatsForNew.EstimatedWaitTime ?? TimeSpan.Zero);

            _logger.LogInformation($"Submitted new slicing job {job.Id} (correlation {envelope.CorrelationId}) for user {request.UserId} with engine {request.SlicerEngine}");

            return new SlicingJobResponse
            {
                JobId = job.Id,
                Status = SlicingJobStatus.Queued,
                EstimatedCompletionTime = estimatedCompletion,
                QueuePosition = (int)infraStatsForNew.QueuedJobs, // Approximate
                SlicerWorkerUrl = new Uri(GetSlicerWorkerUrl(request.SlicerEngine), UriKind.RelativeOrAbsolute)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to submit slicing job for user {userIdForLog}");
            throw;
        }
    }

    public async Task<SlicingJobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            DistributedSlicingJob? job = await _jobQueue.GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                return null;
            }

            SlicingJobStatusResponse response = new()
            {
                JobId = job.Id,
                Status = (SlicingJobStatus)(int)job.Status,
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

            foreach (KeyValuePair<string, object> kv in job.Metadata)
            {
                response.Metadata[kv.Key] = kv.Value;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get job status for {jobId}");
            throw;
        }
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            DistributedSlicingJob? job = await _jobQueue.GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                _logger.LogWarning($"Cannot cancel job {jobId} - job not found");
                return false;
            }

            if (job.Status == SlicingJobStatus.Completed || job.Status == SlicingJobStatus.Error || job.Status == SlicingJobStatus.Cancelled)
            {
                _logger.LogWarning($"Cannot cancel job {jobId} - job is already in final state: {job.Status}");
                return false;
            }

            await _jobQueue.CancelJobAsync(jobId, cancellationToken);

            // Notify about cancellation — map infra job to module job for the notifier interface
            Farm.Slicer.Module.Models.DistributedSlicingJob moduleJob = new()
            {
                Id = job.Id,
                UserId = job.UserId,
                Status = (SlicingJobStatus)(int)job.Status,
                CompletedAt = job.CompletedAt,
                RetryCount = job.RetryCount,
            };
            foreach (KeyValuePair<string, object> kv in job.Metadata)
            {
                moduleJob.Metadata[kv.Key] = kv.Value;
            }

            await _progressNotifier.NotifyFailureAsync(moduleJob, "Job cancelled by user", cancellationToken);

            _logger.LogInformation($"Cancelled slicing job {jobId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to cancel job {jobId}");
            throw;
        }
    }

    public async Task<List<SlicerEngineInfo>> GetAvailableEnginesAsync(CancellationToken cancellationToken = default)
    {
        List<SlicerEngineInfo> engineInfos = new();
        int failures = 0;

        foreach (KeyValuePair<SlicerEngineType, EngineMetadata> kvp in _engineCatalog)
        {
            EngineMetadata meta = kvp.Value;
            try
            {
                SlicerQueueStats queueStats = await _jobQueue.GetQueueStatsAsync((SlicerEngineType)(int)meta.EngineType, cancellationToken);
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
                _logger.LogWarning(ex, $"Queue stats retrieval failed for engine {meta.EngineType}");

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
            InvalidOperationException ex = new("Failed to retrieve queue stats for all slicer engines");
            _logger.LogError(ex, "All engine queue stats retrievals failed");
            throw ex;
        }

        return [.. engineInfos.OrderBy(e => e.Engine)];
    }

    public async Task<Dictionary<SlicerEngineType, SlicerQueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Dictionary<SlicerEngineType, SlicerQueueStats> stats = new();

            foreach (SlicerEngineType engineType in _engineCatalog.Keys)
            {
                SlicerQueueStats infraStats = await _jobQueue.GetQueueStatsAsync((SlicerEngineType)(int)engineType, cancellationToken);
                stats[engineType] = new SlicerQueueStats
                {
                    Engine = engineType,
                    QueuedJobs = infraStats.QueuedJobs,
                    ProcessingJobs = infraStats.ProcessingJobs,
                    CompletedJobs = infraStats.CompletedJobs,
                    FailedJobs = infraStats.FailedJobs,
                    ActiveWorkers = infraStats.ActiveWorkers,
                    AverageProcessingTimeSeconds = infraStats.AverageProcessingTimeSeconds,
                    EstimatedWaitTime = infraStats.EstimatedWaitTime,
                    LastUpdated = infraStats.LastUpdated,
                };
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
            List<DistributedSlicingJob> jobs = await _jobQueue.GetUserJobsAsync(userId, limit, cancellationToken);

            List<SlicingJobStatusResponse> responses = new(jobs.Count);
            foreach (DistributedSlicingJob job in jobs)
            {
                SlicingJobStatusResponse r = new()
                {
                    JobId = job.Id,
                    Status = (SlicingJobStatus)(int)job.Status,
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
                foreach (KeyValuePair<string, object> kv in job.Metadata)
                {
                    r.Metadata[kv.Key] = kv.Value;
                }

                responses.Add(r);
            }

            return responses;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get user jobs for {userId}");
            throw;
        }
    }

    public async Task<SlicerOrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        SlicerOrchestratorHealth health = new()
        {
            IsHealthy = true,
            JobQueueHealthy = true,
            FileStorageHealthy = true
        };

        int engineFailures = 0;

        foreach (KeyValuePair<SlicerEngineType, EngineMetadata> kvp in _engineCatalog)
        {
            EngineMetadata meta = kvp.Value;
            try
            {
                SlicerQueueStats queueStats = await _jobQueue.GetQueueStatsAsync((SlicerEngineType)(int)meta.EngineType, cancellationToken);
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
                _logger.LogWarning(ex, $"Health check failed for engine {meta.EngineType}");
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
            _ = await _jobQueue.GetQueueStatsAsync(null, cancellationToken);
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
            _ = await _fileStorage.FileExistsAsync("health-check-non-existent-file", cancellationToken);
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

        // Validate slicer engine is a defined enum value and available
        if (!Enum.IsDefined(request.SlicerEngine) || !_engineCatalog.TryGetValue(request.SlicerEngine, out EngineMetadata? engineMeta))
        {
            throw new ArgumentException($"Slicer engine {request.SlicerEngine} is not available", nameof(request));
        }

        // Treat obviously-placeholder or invalid model URLs as bad input (argument error) rather than missing files.
        // Tests may pass placeholders like "about:blank" to indicate an empty/invalid model; handle that explicitly.
        Uri modelUrl = request.ModelFileUrl;
        if (modelUrl.IsAbsoluteUri)
        {
            string scheme = modelUrl.Scheme ?? string.Empty;
            if (string.Equals(scheme, "about", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"ModelFileUrl is not a valid model reference: {modelUrl}", nameof(request));
            }
        }

        // Validate file exists and is supported
        bool fileExists = await _fileStorage.FileExistsAsync(request.ModelFileUrl.ToString(), cancellationToken);
        if (!fileExists)
        {
            throw new FileNotFoundException($"Model file not found: {request.ModelFileUrl}");
        }

        // Check file extension
        string extension = Path.GetExtension(request.ModelFileName ?? request.ModelFileUrl.ToString()) ?? string.Empty;
        if (!engineMeta.SupportedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"File extension {extension} is not supported by {request.SlicerEngine}");
        }

        // Validate file size
        SlicerFileMetadata? metadata = await _fileStorage.GetFileMetadataAsync(request.ModelFileUrl.ToString(), cancellationToken);
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
