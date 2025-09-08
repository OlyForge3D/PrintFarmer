using System.Text.Json;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Redis-based implementation of the slicer job queue
/// </summary>
public class RedisSlicerJobQueue : ISlicerJobQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _database;
    private readonly ILogger<RedisSlicerJobQueue> _logger;
    
    // Redis keys
    private readonly string _jobsKey = "slicer:jobs";
    private readonly string _queueKey = "slicer:queue";
    private readonly string _processingKey = "slicer:processing";
    private readonly string _completedKey = "slicer:completed";
    private readonly string _failedKey = "slicer:failed";
    private readonly string _workersKey = "slicer:workers";

    public RedisSlicerJobQueue(IConnectionMultiplexer redis, ILogger<RedisSlicerJobQueue> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _database = redis.GetDatabase();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobJson = JsonSerializer.Serialize(job);
            var score = GetPriorityScore(job.Priority, job.CreatedAt);

            // Use a transaction to ensure atomicity
            var transaction = _database.CreateTransaction();
            
            // Store job details
            var jobKey = GetJobKey(job.Id);
            _ = transaction.HashSetAsync(jobKey, new HashEntry[]
            {
                new("id", job.Id.ToString()),
                new("status", job.Status.ToString()),
                new("engine", job.SlicerEngine.ToString()),
                new("created_at", job.CreatedAt.ToString("O")),
                new("correlation_id", job.CorrelationId.ToString()),
                new("checksum", job.Checksum),
                new("data", jobJson)
            });

            // Store correlation mapping for idempotency
            var correlationKey = GetCorrelationKey(job.CorrelationId, job.Checksum);
            _ = transaction.StringSetAsync(correlationKey, job.Id.ToString(), TimeSpan.FromDays(30));

            // Add to priority queue
            _ = transaction.SortedSetAddAsync(_queueKey, jobJson, score);
            
            // Set job expiration (30 days)
            _ = transaction.KeyExpireAsync(jobKey, TimeSpan.FromDays(30));

            await transaction.ExecuteAsync();

            _logger.LogInformation("Enqueued slicing job {JobId} with priority {Priority} for engine {Engine} (correlation {CorrelationId})", 
                job.Id, job.Priority, job.SlicerEngine, job.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue slicing job {JobId}", job.Id);
            throw;
        }
    }

    public async Task<DistributedSlicingJob?> DequeueAsync(string workerId, SlicerEngineType? preferredEngine = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple dequeue for now - get the highest priority job
            var jobData = await _database.SortedSetPopAsync(_queueKey, Order.Ascending);
            if (!jobData.HasValue)
            {
                return null;
            }

            var job = JsonSerializer.Deserialize<DistributedSlicingJob>(jobData.Value.Element!);
            
            if (job != null && preferredEngine != null && job.SlicerEngine != preferredEngine)
            {
                // Re-queue if engine doesn't match preference
                await RequeueJobAsync(job);
                return null;
            }

            if (job != null)
            {
                job.Status = SlicingJobStatus.Slicing;
                job.StartedAt = DateTime.UtcNow;
                job.WorkerId = workerId;
                
                // Move to processing queue
                await _database.SortedSetAddAsync(_processingKey, JsonSerializer.Serialize(job), jobData.Value.Score);
                
                await UpdateJobAsync(job);
                
                _logger.LogInformation("Dequeued slicing job {JobId} for worker {WorkerId}", job.Id, workerId);
            }

            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dequeue job for worker {WorkerId}", workerId);
            throw;
        }
    }

    public async Task CompleteJobAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
    {
        try
        {
            job.Status = result.Success ? SlicingJobStatus.Completed : SlicingJobStatus.Error;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = result.Error;
            job.ResultFileUrl = result.ResultFileUrl;
            job.EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds;
            job.EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams;
            job.LayerCount = result.LayerCount;
            job.OutputFileSizeBytes = result.OutputFileSizeBytes;

            var jobJson = JsonSerializer.Serialize(job);
            var targetQueue = result.Success ? _completedKey : _failedKey;

            // Remove from processing and add to completed/failed
            await _database.SortedSetRemoveAsync(_processingKey, jobJson);
            await _database.SortedSetAddAsync(targetQueue, jobJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await UpdateJobAsync(job);

            _logger.LogInformation("Completed slicing job {JobId} with status {Status}", job.Id, job.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete job {JobId}", job.Id);
            throw;
        }
    }

    public async Task FailJobAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            var job = await GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                _logger.LogWarning("Cannot fail job {JobId} - job not found", jobId);
                return;
            }

            var result = new SlicingResult { Success = false, Error = errorMessage };
            await CompleteJobAsync(job, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fail job {JobId}", jobId);
            throw;
        }
    }

    public async Task UpdateProgressAsync(Guid jobId, int progress, string? currentStep = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobKey = GetJobKey(jobId);
            var updates = new HashEntry[]
            {
                new("progress", progress),
                new("updated_at", DateTime.UtcNow.ToString("O"))
            };

            if (currentStep != null)
            {
                updates = updates.Append(new HashEntry("current_step", currentStep)).ToArray();
            }

            await _database.HashSetAsync(jobKey, updates);
            
            // Also update the job data if we have it
            var job = await GetJobAsync(jobId, cancellationToken);
            if (job != null)
            {
                job.Progress = progress;
                await UpdateJobAsync(job);
            }

            _logger.LogDebug("Updated progress for job {JobId}: {Progress}%", jobId, progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update progress for job {JobId}", jobId);
            throw;
        }
    }

    public async Task<DistributedSlicingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobKey = GetJobKey(jobId);
            var jobData = await _database.HashGetAsync(jobKey, "data");
            
            if (!jobData.HasValue)
            {
                return null;
            }

            return JsonSerializer.Deserialize<DistributedSlicingJob>(jobData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job {JobId}", jobId);
            throw;
        }
    }

    public async Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var job = await GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                _logger.LogWarning("Cannot cancel job {JobId} - job not found", jobId);
                return;
            }

            job.Status = SlicingJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;

            var jobJson = JsonSerializer.Serialize(job);

            // Remove from queues and add to completed
            await _database.SortedSetRemoveAsync(_queueKey, jobJson);
            await _database.SortedSetRemoveAsync(_processingKey, jobJson);
            await _database.SortedSetAddAsync(_completedKey, jobJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await UpdateJobAsync(job);

            _logger.LogInformation("Cancelled slicing job {JobId}", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel job {JobId}", jobId);
            throw;
        }
    }

    public async Task<SlicerQueueStats> GetQueueStatsAsync(SlicerEngineType? engine = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var queuedCount = await _database.SortedSetLengthAsync(_queueKey);
            var processingCount = await _database.SortedSetLengthAsync(_processingKey);
            var completedCount = await _database.SortedSetLengthAsync(_completedKey);
            var failedCount = await _database.SortedSetLengthAsync(_failedKey);

            return new SlicerQueueStats
            {
                Engine = engine ?? SlicerEngineType.OrcaSlicer,
                QueuedJobs = queuedCount,
                ProcessingJobs = processingCount,
                CompletedJobs = completedCount,
                FailedJobs = failedCount,
                ActiveWorkers = 0, // Simplified for now
                EstimatedWaitTime = EstimateWaitTime(queuedCount, 1),
                LastUpdated = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue stats");
            throw;
        }
    }

    public async Task<List<DistributedSlicingJob>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobs = new List<DistributedSlicingJob>();
            
            // Simplified implementation - scan through recent completed and failed jobs
            var completedJobs = await _database.SortedSetRangeByRankAsync(_completedKey, 0, limit ?? 100, Order.Descending);
            var failedJobs = await _database.SortedSetRangeByRankAsync(_failedKey, 0, limit ?? 100, Order.Descending);
            
            foreach (var jobJson in completedJobs.Concat(failedJobs))
            {
                var job = JsonSerializer.Deserialize<DistributedSlicingJob>(jobJson);
                if (job?.UserId == userId)
                {
                    jobs.Add(job);
                }
            }

            return jobs.OrderByDescending(j => j.CreatedAt).Take(limit ?? 100).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user jobs for {UserId}", userId);
            throw;
        }
    }

    public async Task CleanupOldJobsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffTimestamp = DateTimeOffset.UtcNow.Subtract(maxAge).ToUnixTimeSeconds();
            
            var removedCompleted = await _database.SortedSetRemoveRangeByScoreAsync(_completedKey, 0, cutoffTimestamp);
            var removedFailed = await _database.SortedSetRemoveRangeByScoreAsync(_failedKey, 0, cutoffTimestamp);

            _logger.LogInformation("Cleaned up {CompletedCount} completed and {FailedCount} failed jobs older than {MaxAge}", 
                removedCompleted, removedFailed, maxAge);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old jobs");
            throw;
        }
    }

    public async Task RequeueFailedJobsAsync(int maxRetryCount = 3, CancellationToken cancellationToken = default)
    {
        try
        {
            var failedJobs = await _database.SortedSetRangeByRankAsync(_failedKey, 0, 100);
            var requeuedCount = 0;

            foreach (var jobJson in failedJobs)
            {
                var job = JsonSerializer.Deserialize<DistributedSlicingJob>(jobJson);
                if (job != null && job.RetryCount < maxRetryCount)
                {
                    job.Status = SlicingJobStatus.Queued;
                    job.RetryCount++;
                    job.LastRetryAt = DateTime.UtcNow;
                    job.ErrorMessage = null;
                    job.StartedAt = null;
                    job.CompletedAt = null;
                    job.WorkerId = null;

                    // Remove from failed queue and re-enqueue
                    await _database.SortedSetRemoveAsync(_failedKey, jobJson);
                    await EnqueueAsync(job, cancellationToken);
                    requeuedCount++;
                }
            }

            _logger.LogInformation("Requeued {Count} failed jobs for retry", requeuedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue failed jobs");
            throw;
        }
    }

    private async Task UpdateJobAsync(DistributedSlicingJob job)
    {
        var jobKey = GetJobKey(job.Id);
        var jobJson = JsonSerializer.Serialize(job);
        
        await _database.HashSetAsync(jobKey, new HashEntry[]
        {
            new("status", job.Status.ToString()),
            new("progress", job.Progress),
            new("updated_at", DateTime.UtcNow.ToString("O")),
            new("data", jobJson)
        });
    }

    private async Task RequeueJobAsync(DistributedSlicingJob job)
    {
        var jobJson = JsonSerializer.Serialize(job);
        var score = GetPriorityScore(job.Priority, job.CreatedAt);
        
        await _database.SortedSetAddAsync(_queueKey, jobJson, score);
    }

    private static string GetJobKey(Guid jobId) => $"slicer:job:{jobId}";

    private static double GetPriorityScore(SlicingJobPriority priority, DateTime createdAt)
    {
        // Lower score = higher priority
        var priorityValue = priority switch
        {
            SlicingJobPriority.Critical => 0.0,
            SlicingJobPriority.High => 1000.0,
            SlicingJobPriority.Normal => 2000.0,
            SlicingJobPriority.Low => 3000.0,
            _ => 2000.0
        };
        
        // Add timestamp component to maintain FIFO within same priority
        var timestampComponent = createdAt.Ticks / 10000000.0; // Convert to seconds
        return priorityValue + timestampComponent;
    }

    private static TimeSpan EstimateWaitTime(long queuedJobs, int activeWorkers)
    {
        if (activeWorkers == 0) return TimeSpan.FromHours(24); // Unknown
        
        // Very rough estimate: assume 5 minutes per job on average
        var averageJobTime = TimeSpan.FromMinutes(5);
        var estimatedSeconds = (queuedJobs * averageJobTime.TotalSeconds) / activeWorkers;
        
        return TimeSpan.FromSeconds(Math.Min(estimatedSeconds, TimeSpan.FromHours(24).TotalSeconds));
    }

    public async Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default)
    {
        try
        {
            var correlationKey = GetCorrelationKey(correlationId, checksum);
            var jobIdString = await _database.StringGetAsync(correlationKey);
            
            if (!jobIdString.HasValue) return null;
            
            if (!Guid.TryParse(jobIdString, out var jobId)) return null;
            
            return await GetJobAsync(jobId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find existing job for correlation {CorrelationId}", correlationId);
            throw;
        }
    }

    public async Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default)
    {
        try
        {
            var correlationKey = GetCorrelationKey(correlationId, checksum);
            return await _database.KeyExistsAsync(correlationKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check job existence for correlation {CorrelationId}", correlationId);
            throw;
        }
    }

    private string GetCorrelationKey(Guid correlationId, string checksum)
    {
        return $"slicer:correlation:{correlationId}:{checksum}";
    }
}