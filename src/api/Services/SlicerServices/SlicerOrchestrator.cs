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
    private readonly Dictionary<SlicerEngineType, ISlicerEngine> _slicerEngines;

    public SlicerOrchestrator(
        ISlicerJobQueue jobQueue,
        ISlicerFileStorage fileStorage,
        ISlicerProgressNotifier progressNotifier,
        IEnumerable<ISlicerEngine> slicerEngines,
        ILogger<SlicerOrchestrator> logger)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _progressNotifier = progressNotifier ?? throw new ArgumentNullException(nameof(progressNotifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _slicerEngines = slicerEngines.ToDictionary(e => e.EngineType, e => e);
    }

    public async Task<SlicingJobResponse> SubmitJobAsync(SlicingJobRequest request, CancellationToken cancellationToken = default)
    {
    // Capture for logging after null check
    // Null guard (CA1062)
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
                    EstimatedCompletionTime = existingJob.CompletedAt ?? DateTime.UtcNow.Add(queueStats.EstimatedWaitTime),
                    QueuePosition = existingJob.Status == SlicingJobStatus.Queued ? (int)queueStats.QueuedJobs : 0,
                    SlicerWorkerUrl = GetSlicerWorkerUrl(request.SlicerEngine)
                };
            }

            // Validate checksum if envelope was provided externally
            if (request.Envelope != null)
            {
                // Create content for checksum validation (fix: removed invalid 'Slicer.Messaging' prefix)
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
                var fileMetadata = await _fileStorage.GetFileMetadataAsync(request.ModelFileUrl, cancellationToken);
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
            var estimatedCompletion = DateTime.UtcNow.Add(queueStatsForNew.EstimatedWaitTime);

            _logger.LogInformation("Submitted new slicing job {JobId} (correlation {CorrelationId}) for user {UserId} with engine {SlicerEngine}", 
                job.Id, envelope.CorrelationId, request.UserId, request.SlicerEngine);

            return new SlicingJobResponse
            {
                JobId = job.Id,
                Status = SlicingJobStatus.Queued,
                EstimatedCompletionTime = estimatedCompletion,
                QueuePosition = (int)queueStatsForNew.QueuedJobs, // Approximate
                SlicerWorkerUrl = GetSlicerWorkerUrl(request.SlicerEngine)
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

            return new SlicingJobStatusResponse
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
                Metadata = job.Metadata
            };
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
        try
        {
            var engineInfos = new List<SlicerEngineInfo>();

            foreach (var kvp in _slicerEngines)
            {
                var engine = kvp.Value;
                var queueStats = await _jobQueue.GetQueueStatsAsync(engine.EngineType, cancellationToken);
                var isHealthy = await engine.IsHealthyAsync(cancellationToken);

                engineInfos.Add(new SlicerEngineInfo
                {
                    Engine = engine.EngineType,
                    Version = engine.Version,
                    IsHealthy = isHealthy,
                    ActiveWorkers = queueStats.ActiveWorkers,
                    QueueDepth = queueStats.QueuedJobs,
                    SupportedExtensions = engine.SupportedFileExtensions,
                    EstimatedWaitTime = queueStats.EstimatedWaitTime
                });
            }

            return [.. engineInfos.OrderBy(e => e.Engine)];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available engines");
            throw;
        }
    }

    public async Task<Dictionary<SlicerEngineType, SlicerQueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = new Dictionary<SlicerEngineType, SlicerQueueStats>();

            foreach (var engineType in _slicerEngines.Keys)
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
            
            return [.. jobs.Select(job => new SlicingJobStatusResponse
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
                Metadata = job.Metadata
            })];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user jobs for {UserId}", userId);
            throw;
        }
    }

    public async Task<SlicerOrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = new SlicerOrchestratorHealth
            {
                IsHealthy = true,
                JobQueueHealthy = true,
                FileStorageHealthy = true
            };

            // Check each engine
            foreach (var kvp in _slicerEngines)
            {
                var engine = kvp.Value;
                var queueStats = await _jobQueue.GetQueueStatsAsync(engine.EngineType, cancellationToken);
                var isHealthy = await engine.IsHealthyAsync(cancellationToken);

                health.Engines[engine.EngineType] = new SlicerEngineInfo
                {
                    Engine = engine.EngineType,
                    Version = engine.Version,
                    IsHealthy = isHealthy,
                    ActiveWorkers = queueStats.ActiveWorkers,
                    QueueDepth = queueStats.QueuedJobs,
                    SupportedExtensions = engine.SupportedFileExtensions,
                    EstimatedWaitTime = queueStats.EstimatedWaitTime
                };

                health.TotalActiveJobs += queueStats.ActiveWorkers;
                health.TotalQueuedJobs += queueStats.QueuedJobs;

                if (!isHealthy)
                {
                    health.IsHealthy = false;
                }
            }

            // Test job queue connectivity
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

            // Test file storage connectivity (simplified test)
            try
            {
                // This is a simple connectivity test - in production you might want a dedicated health check method
                await _fileStorage.FileExistsAsync("health-check-non-existent-file", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "File storage health check failed");
                health.FileStorageHealthy = false;
                health.IsHealthy = false;
            }

            return health;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get orchestrator health");
            return new SlicerOrchestratorHealth { IsHealthy = false };
        }
    }

    private async Task ValidateRequestAsync(SlicingJobRequest request, CancellationToken cancellationToken)
    {
        // Validate required fields
    ArgumentNullException.ThrowIfNull(request);

        if (request.UserId == Guid.Empty)
        {
            throw new ArgumentException("UserId is required", nameof(request));
        }
        if (request.PrinterId == Guid.Empty)
        {
            throw new ArgumentException("PrinterId is required", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.ModelFileUrl))
        {
            throw new ArgumentException("ModelFileUrl is required", nameof(request));
        }

        // Validate slicer engine is available
        if (!_slicerEngines.TryGetValue(request.SlicerEngine, out var engine))
        {
            throw new ArgumentException($"Slicer engine {request.SlicerEngine} is not available", nameof(request));
        }

        // Check if slicer engine is healthy
        var isHealthy = await engine.IsHealthyAsync(cancellationToken);
        if (!isHealthy)
        {
            throw new InvalidOperationException($"Slicer engine {request.SlicerEngine} is currently unavailable");
        }

        // Validate file exists and is supported
        var fileExists = await _fileStorage.FileExistsAsync(request.ModelFileUrl, cancellationToken);
        if (!fileExists)
        {
            throw new FileNotFoundException($"Model file not found: {request.ModelFileUrl}");
        }

        // Check file extension
        var extension = Path.GetExtension(request.ModelFileName ?? request.ModelFileUrl).ToLowerInvariant();
        if (!engine.SupportedFileExtensions.Contains(extension))
        {
            throw new ArgumentException($"File extension {extension} is not supported by {request.SlicerEngine}");
        }

        // Validate file size
        var metadata = await _fileStorage.GetFileMetadataAsync(request.ModelFileUrl, cancellationToken);
        if (metadata != null && metadata.SizeBytes > 100_000_000) // 100MB limit
        {
            throw new ArgumentException("File size exceeds maximum limit of 100MB");
        }
    }

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