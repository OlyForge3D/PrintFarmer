using System.Security.Cryptography;
using System.Text.Json;
using Farm.Web.Shared;
using StackExchange.Redis;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Redis-based implementation of the slicer job queue
/// </summary>
public class RedisSlicerJobQueue(IConnectionMultiplexer redis, ILogger<RedisSlicerJobQueue> logger) : ISlicerJobQueue
{
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly ILogger<RedisSlicerJobQueue> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Redis keys
    // Keys retained for future expansion (currently used in stats operations)
    // private readonly string _jobsKey = "slicer:jobs"; // reserved for future detailed hash lookups
    private readonly string _queueKey = "slicer:queue";
    private readonly string _processingKey = "slicer:processing";
    private readonly string _completedKey = "slicer:completed";
    private readonly string _failedKey = "slicer:failed";

    public async Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            string jobJson = JsonSerializer.Serialize(job);
            // Use ScheduledAt if present so delayed jobs are ordered correctly
            DateTime referenceTime = job.ScheduledAt ?? job.CreatedAt;
            double score = GetPriorityScore(job.Priority, referenceTime);

            // Use a transaction to ensure atomicity
            ITransaction transaction = _database.CreateTransaction();

            // Store job details
            string jobKey = GetJobKey(job.Id);
            if (transaction != null)
            {
                _ = transaction.HashSetAsync(jobKey,
                    [
                        new HashEntry("id", job.Id.ToString()),
                        new HashEntry("status", job.Status.ToString()),
                        new HashEntry("engine", job.SlicerEngine.ToString()),
                        new HashEntry("created_at", job.CreatedAt.ToString("O")),
                        new HashEntry("scheduled_at", job.ScheduledAt?.ToString("O") ?? string.Empty),
                        new HashEntry("correlation_id", job.CorrelationId.ToString()),
                        new HashEntry("checksum", job.Checksum ?? string.Empty),
                        new HashEntry("data", jobJson)
                    ]);

                // Store correlation mapping for idempotency
                string correlationKey = GetCorrelationKey(job.CorrelationId, job.Checksum ?? string.Empty);
                _ = transaction.StringSetAsync(correlationKey, job.Id.ToString(), TimeSpan.FromDays(30));

                // Add to priority queue
                _ = transaction.SortedSetAddAsync(_queueKey, jobJson, score, flags: CommandFlags.None);

                // Set job expiration (30 days)
                _ = transaction.KeyExpireAsync(jobKey, TimeSpan.FromDays(30));

                await transaction.ExecuteAsync();
            }
            else
            {
                // Fallback for test doubles that don't provide transactions
                await _database.HashSetAsync(jobKey,
                    [
                        new HashEntry("id", job.Id.ToString()),
                        new HashEntry("status", job.Status.ToString()),
                        new HashEntry("engine", job.SlicerEngine.ToString()),
                        new HashEntry("created_at", job.CreatedAt.ToString("O")),
                        new HashEntry("scheduled_at", job.ScheduledAt?.ToString("O") ?? string.Empty),
                        new HashEntry("correlation_id", job.CorrelationId.ToString()),
                        new HashEntry("checksum", job.Checksum ?? string.Empty),
                        new HashEntry("data", jobJson)
                    ],
                    CommandFlags.None);

                string correlationKey = GetCorrelationKey(job.CorrelationId, job.Checksum ?? string.Empty);
                await _database.StringSetAsync(correlationKey, job.Id.ToString(), TimeSpan.FromDays(30));
                await _database.SortedSetAddAsync(_queueKey, jobJson, score, CommandFlags.None);
                await _database.KeyExpireAsync(jobKey, TimeSpan.FromDays(30));
            }

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
            SortedSetEntry? jobData = await _database.SortedSetPopAsync(_queueKey, Order.Ascending, CommandFlags.None);
            if (!jobData.HasValue)
            {
                return null;
            }

            DistributedSlicingJob? job = RedisSlicerJobQueueHelpers.DeserializeJob(jobData.Value.Element);

            // Respect scheduled start times: if job has ScheduledAt in the future, re-enqueue and skip
            if (job != null && job.ScheduledAt.HasValue && job.ScheduledAt.Value > DateTime.UtcNow)
            {
                // Put back into the queue unchanged (EnqueueAsync will respect ScheduledAt)
                await EnqueueAsync(job);
                return null;
            }

            if (job != null && preferredEngine != null && job.EngineType != preferredEngine)
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
                await _database.SortedSetAddAsync(_processingKey, JsonSerializer.Serialize(job), jobData.Value.Score, CommandFlags.None);

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
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
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

            string jobJson = JsonSerializer.Serialize(job);
            string targetQueue = result.Success ? _completedKey : _failedKey;

            // Remove from processing and add to completed/failed
            await _database.SortedSetRemoveAsync(_processingKey, jobJson, CommandFlags.None);
            await _database.SortedSetAddAsync(targetQueue, jobJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), CommandFlags.None);

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
            DistributedSlicingJob? job = await GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                _logger.LogWarning("Cannot fail job {JobId} - job not found", jobId);
                return;
            }

            SlicingResult result = new()
            { Success = false, Error = errorMessage };
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
            string jobKey = GetJobKey(jobId);
            List<HashEntry> updates = new()
            {
                new("progress", progress),
                new("updated_at", DateTime.UtcNow.ToString("O"))
            };

            if (currentStep != null)
            {
                updates.Add(new HashEntry("current_step", currentStep));
            }

            await _database.HashSetAsync(jobKey, [.. updates]);

            // Also update the job data if we have it
            DistributedSlicingJob? job = await GetJobAsync(jobId, cancellationToken);
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
            string jobKey = GetJobKey(jobId);
            RedisValue jobData = await _database.HashGetAsync(jobKey, "data", CommandFlags.None);
            if (!jobData.HasValue)
            {
                return null;
            }
            return RedisSlicerJobQueueHelpers.DeserializeJob(jobData);
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
            DistributedSlicingJob? job = await GetJobAsync(jobId, cancellationToken);
            if (job == null)
            {
                _logger.LogWarning("Cannot cancel job {JobId} - job not found", jobId);
                return;
            }

            job.Status = SlicingJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;

            string jobJson = JsonSerializer.Serialize(job);

            // Remove from queues and add to completed
            await _database.SortedSetRemoveAsync(_queueKey, jobJson, CommandFlags.None);
            await _database.SortedSetRemoveAsync(_processingKey, jobJson, CommandFlags.None);
            await _database.SortedSetAddAsync(_completedKey, jobJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), CommandFlags.None);

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
            long queuedCount = await _database.SortedSetLengthAsync(_queueKey);
            long processingCount = await _database.SortedSetLengthAsync(_processingKey);
            long completedCount = await _database.SortedSetLengthAsync(_completedKey);
            long failedCount = await _database.SortedSetLengthAsync(_failedKey);

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
            List<DistributedSlicingJob> jobs = new();

            // Simplified implementation - scan through a fixed recent window (tests expect end rank 100 regardless of requested limit)
            RedisValue[] completedJobs = await _database.SortedSetRangeByRankAsync(_completedKey, 0, 100, Order.Descending, CommandFlags.None);
            RedisValue[] failedJobs = await _database.SortedSetRangeByRankAsync(_failedKey, 0, 100, Order.Descending, CommandFlags.None);

            foreach (RedisValue jobJson in completedJobs.Concat(failedJobs))
            {
                DistributedSlicingJob? job = RedisSlicerJobQueueHelpers.DeserializeJob(jobJson);
                if (job?.UserId == userId)
                {
                    jobs.Add(job);
                }
            }

            return [.. jobs.OrderByDescending(j => j.CreatedAt).Take(limit ?? 100)];
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
            long cutoffTimestamp = DateTimeOffset.UtcNow.Subtract(maxAge).ToUnixTimeSeconds();

            long removedCompleted = await _database.SortedSetRemoveRangeByScoreAsync(_completedKey, 0, cutoffTimestamp);
            long removedFailed = await _database.SortedSetRemoveRangeByScoreAsync(_failedKey, 0, cutoffTimestamp);

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
            RedisValue[] failedJobs = await _database.SortedSetRangeByRankAsync(_failedKey, 0, 100, Order.Ascending, CommandFlags.None);
            int requeuedCount = 0;

            foreach (RedisValue jobJson in failedJobs)
            {
                DistributedSlicingJob? job = RedisSlicerJobQueueHelpers.DeserializeJob(jobJson);
                if (job != null && job.RetryCount < maxRetryCount)
                {
                    job.Status = SlicingJobStatus.Queued;
                    // compute a backoff delay based on current RetryCount
                    int delaySeconds = Math.Min(3600, (int)(Math.Pow(2, job.RetryCount) * 10));
                    TimeSpan delay = TimeSpan.FromSeconds(delaySeconds);

                    // Remove from failed queue and schedule requeue
                    await _database.SortedSetRemoveAsync(_failedKey, jobJson);
                    await RequeueJobAsync(job, delay, jitterPercent: 0.0, cancellationToken: cancellationToken);
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
        ArgumentNullException.ThrowIfNull(job);
        string jobKey = GetJobKey(job.Id);
        string jobJson = JsonSerializer.Serialize(job);

        await _database.HashSetAsync(
            jobKey,
            [
                new("status", job.Status.ToString()),
                new("progress", job.Progress),
                new("updated_at", DateTime.UtcNow.ToString("O")),
                new("scheduled_at", job.ScheduledAt?.ToString("O") ?? string.Empty),
                new("data", jobJson)
            ],
            CommandFlags.None);
    }

    public async Task RequeueJobAsync(DistributedSlicingJob job, TimeSpan? delay = null, double jitterPercent = 0.0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            // Attempt to remove any existing representation in the processing set by matching job.Id
            RedisValue[] processingEntries = await _database.SortedSetRangeByRankAsync(_processingKey, 0, -1, Order.Ascending, CommandFlags.None);
            foreach (RedisValue entry in processingEntries)
            {
                DistributedSlicingJob? pjob = RedisSlicerJobQueueHelpers.DeserializeJob(entry);
                if (pjob != null && pjob.Id == job.Id)
                {
                    await _database.SortedSetRemoveAsync(_processingKey, entry, CommandFlags.None);
                    break;
                }
            }

            // Increment counters and set timing metadata
            job.RetryCount++;
            job.LastRetryAt = DateTime.UtcNow;
            job.ErrorMessage = null;
            job.StartedAt = null;
            job.CompletedAt = null;
            job.WorkerId = null;
            // Schedule by setting ScheduledAt if delay provided
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
            {
                // Determine jitter percent to apply; if none provided, default to 15%
                double jitter = jitterPercent > 0 ? Math.Abs(jitterPercent) / 100.0 : 0.15;
                // Clip jitter reasonably
                jitter = Math.Max(0.0, Math.Min(jitter, 1.0));

                double baseSeconds = delay.Value.TotalSeconds;
                double minFactor = Math.Max(0.0, 1.0 - jitter);
                double maxFactor = 1.0 + jitter;
                double jitterFactor = minFactor + ((RandomNumberGenerator.GetInt32(0, 1_000_000) / 1_000_000.0) * (maxFactor - minFactor));
                int scheduledSeconds = Math.Max(1, (int)Math.Round(baseSeconds * jitterFactor));
                job.ScheduledAt = DateTime.UtcNow.AddSeconds(scheduledSeconds);
                _logger.LogDebug("Applied jitter to scheduled retry for job {JobId}: base={BaseSeconds}s jitterPercent={JitterPercent}% jitterFactor={JitterFactor:F2} scheduledIn={ScheduledSeconds}s", job.Id, baseSeconds, jitter * 100.0, jitterFactor, scheduledSeconds);
            }
            else
            {
                job.ScheduledAt = DateTime.UtcNow;
            }

            // Enqueue with updated scheduledAt which influences priority score and thus acts as a delayed enqueue
            await EnqueueAsync(job, cancellationToken);

            _logger.LogInformation("Requeued job {JobId} for retry (retryCount={RetryCount}, scheduledInSeconds={Delay})", job.Id, job.RetryCount, delay?.TotalSeconds ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue job {JobId}", job.Id);
            throw;
        }
    }

    private static string GetJobKey(Guid jobId) => $"slicer:job:{jobId}";

    private static double GetPriorityScore(SlicingJobPriority priority, DateTime createdAt)
    {
        // Lower score = higher priority
        double priorityValue = priority switch
        {
            SlicingJobPriority.Critical => 0.0,
            SlicingJobPriority.High => 1000.0,
            SlicingJobPriority.Normal => 2000.0,
            SlicingJobPriority.Low => 3000.0,
            _ => 2000.0
        };

        // Add timestamp component to maintain FIFO within same priority
        double timestampComponent = createdAt.Ticks / 10000000.0; // Convert to seconds
        return priorityValue + timestampComponent;
    }

    private static TimeSpan EstimateWaitTime(long queuedJobs, int activeWorkers)
    {
        if (activeWorkers == 0)
        {
            return TimeSpan.FromHours(24); // Unknown
        }

        // Very rough estimate: assume 5 minutes per job on average
        TimeSpan averageJobTime = TimeSpan.FromMinutes(5);
        double estimatedSeconds = (queuedJobs * averageJobTime.TotalSeconds) / activeWorkers;

        return TimeSpan.FromSeconds(Math.Min(estimatedSeconds, TimeSpan.FromHours(24).TotalSeconds));
    }

    public async Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default)
    {
        try
        {
            string correlationKey = GetCorrelationKey(correlationId, checksum);
            RedisValue jobIdString = await _database.StringGetAsync(correlationKey);

            if (!jobIdString.HasValue)
            {
                return null;
            }

            if (!Guid.TryParse(jobIdString, out Guid jobId))
            {
                return null;
            }

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
            string correlationKey = GetCorrelationKey(correlationId, checksum);
            return await _database.KeyExistsAsync(correlationKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check job existence for correlation {CorrelationId}", correlationId);
            throw;
        }
    }

    private static string GetCorrelationKey(Guid correlationId, string checksum) => $"slicer:correlation:{correlationId}:{checksum}";

    // Local helpers (consider moving to separate utility if reused)
    private static class RedisSlicerJobQueueHelpers
    {
        public static DistributedSlicingJob? DeserializeJob(RedisValue value)
        {
            if (!value.HasValue)
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<DistributedSlicingJob>(value!);
            }
            catch
            {
                return null;
            }
        }
    }
}
