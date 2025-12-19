using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using StackExchange.Redis;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Redis-backed distributed implementation of harvest file processing queue.
/// Persists jobs across application restarts and enables distributed processing.
/// </summary>
public sealed class RedisHarvestQueue : IHarvestQueue, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IUnifiedLoggingService _logger;
    private readonly string _queueKey = "harvest:queue"; // Primary queue
    private readonly string _processingKey = "harvest:processing"; // Jobs being processed
    private readonly string _completedKey = "harvest:completed"; // Completed job IDs
    private readonly string _indexKey = "harvest:index"; // For depth tracking
    private bool _disposed;
    private bool _completionRequested;
    private const int CHANNEL_READ_BATCH_SIZE = 10; // Read multiple items at once
    private const int CLEANUP_BATCH_SIZE = 100; // Clean up completed jobs in batches

    public RedisHarvestQueue(IConnectionMultiplexer redis, IUnifiedLoggingService logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _logger.LogInformation("RedisHarvestQueue initialized", null, null);

        // Clean up stale data on startup
        _ = CleanupStaleDataAsync();
    }

    public async Task EnqueueAsync(HarvestFileJob job, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(RedisHarvestQueue));
        ArgumentNullException.ThrowIfNull(job);

        if (_completionRequested)
        {
            _logger.LogWarning("Attempted to enqueue job after queue completion requested", null, null);
            throw new InvalidOperationException("Queue is completing - no new jobs accepted");
        }

        try
        {
            var db = _redis.GetDatabase();
            var jobJson = JsonSerializer.Serialize(job);
            var jobId = $"{job.OperationId}:{job.FileName}";

            // Use sorted set for queue with score = timestamp for FIFO ordering
            await db.SortedSetAddAsync(
                _queueKey,
                jobJson,
                DateTime.UtcNow.Ticks,
                flags: CommandFlags.FireAndForget);

            // Increment queue depth counter
            await db.StringIncrementAsync(_indexKey, flags: CommandFlags.FireAndForget);

            _logger.LogDebug(
                $"Enqueued job {jobId} for operation {job.OperationId}",
                null,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                $"Failed to enqueue job {job.FileName}",
                null,
                null);
            throw;
        }
    }

    public async IAsyncEnumerable<HarvestFileJob> DequeueAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed)
        {
            yield break;
        }

        var db = _redis.GetDatabase();
        var batchYieldCount = 0;

        while (!ct.IsCancellationRequested)
        {
            List<HarvestFileJob>? jobsToYield = null;

            try
            {
                // Get batch of jobs (sorted by score/timestamp for FIFO)
                var jobs = await db.SortedSetRangeByRankAsync(
                    _queueKey,
                    0,
                    CHANNEL_READ_BATCH_SIZE - 1,
                    order: Order.Ascending);

                if (jobs.Length == 0)
                {
                    // No jobs available - if completion requested, stop
                    if (_completionRequested)
                    {
                        _logger.LogInformation("Queue dequeue completed - completion was requested", null, null);
                        yield break;
                    }

                    // Wait a bit before retrying (avoid busy loop)
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                    continue;
                }

                jobsToYield = new List<HarvestFileJob>();

                foreach (var jobJson in jobs)
                {
                    if (jobJson.IsNull)
                        continue;

                    try
                    {
                        var job = JsonSerializer.Deserialize<HarvestFileJob>(jobJson.ToString());
                        if (job != null)
                        {
                            // Move to processing set
                            var timestamp = DateTime.UtcNow.Ticks;
                            await db.SortedSetAddAsync(
                                _processingKey,
                                jobJson,
                                timestamp);

                            // Remove from main queue
                            await db.SortedSetRemoveAsync(_queueKey, jobJson);

                            _logger.LogDebug(
                                $"Dequeued job {job.FileName} from operation {job.OperationId}",
                                null,
                                null);

                            jobsToYield.Add(job);
                            batchYieldCount++;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(
                            ex,
                            "Failed to deserialize harvest job from Redis",
                            null,
                            null);
                        // Remove malformed job
                        await db.SortedSetRemoveAsync(_queueKey, jobJson);
                    }
                }

                // Periodic cleanup of completed jobs
                if (batchYieldCount % 50 == 0)
                {
                    _ = CleanupCompletedJobsAsync();
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(
                    ex,
                    "Error during queue dequeue operation",
                    null,
                    null);

                // Wait before retrying
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            // Yield all collected jobs after try-catch block
            if (jobsToYield != null)
            {
                foreach (var job in jobsToYield)
                {
                    yield return job;
                }
            }
        }
    }

    public int QueueDepth
    {
        get
        {
            if (_disposed)
                return 0;

            try
            {
                var db = _redis.GetDatabase();
                var count = db.StringGet(_indexKey);
                return count.HasValue && long.TryParse(count.ToString(), out var depth) ? (int)depth : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to get queue depth from Redis",
                    null,
                    null);
                return 0;
            }
        }
    }

    public void CompleteAdding()
    {
        _completionRequested = true;
        _logger.LogInformation("CompleteAdding() called - harvest queue will stop after current jobs", null, null);
    }

    /// <summary>
    /// Mark a job as completed (moved from processing to completed tracking)
    /// </summary>
    public async Task MarkCompletedAsync(HarvestFileJob job, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var jobJson = JsonSerializer.Serialize(job);

            // Move from processing to completed
            await db.SortedSetRemoveAsync(_processingKey, jobJson);
            await db.SortedSetAddAsync(
                _completedKey,
                jobJson,
                DateTime.UtcNow.Ticks);

            _logger.LogDebug(
                $"Marked job {job.FileName} as completed",
                null,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                $"Failed to mark job {job.FileName} as completed",
                null,
                null);
        }
    }

    /// <summary>
    /// Mark a job as failed (moved from processing, can be requeued)
    /// </summary>
    public async Task MarkFailedAsync(HarvestFileJob job, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var jobJson = JsonSerializer.Serialize(job);

            // Remove from processing, re-add to queue with lower priority (higher score)
            await db.SortedSetRemoveAsync(_processingKey, jobJson);
            await db.SortedSetAddAsync(
                _queueKey,
                jobJson,
                DateTime.UtcNow.AddMinutes(1).Ticks); // Re-queue after delay

            _logger.LogDebug(
                $"Marked job {job.FileName} as failed - requeued with delay",
                null,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                $"Failed to mark job {job.FileName} as failed",
                null,
                null);
        }
    }

    /// <summary>
    /// Get current queue statistics
    /// </summary>
    public async Task<HarvestQueueStats> GetStatsAsync()
    {
        try
        {
            var db = _redis.GetDatabase();
            var queued = await db.SortedSetLengthAsync(_queueKey);
            var processing = await db.SortedSetLengthAsync(_processingKey);
            var completed = await db.SortedSetLengthAsync(_completedKey);

            return new HarvestQueueStats
            {
                QueuedCount = (int)queued,
                ProcessingCount = (int)processing,
                CompletedCount = (int)completed,
                TotalCount = (int)(queued + processing + completed),
                IsCompletionRequested = _completionRequested
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get queue stats", null, null);
            return new HarvestQueueStats { TotalCount = 0 };
        }
    }

    private async Task CleanupStaleDataAsync()
    {
        try
        {
            var db = _redis.GetDatabase();

            // Remove processing items older than 24 hours (likely crashed/stalled)
            var cutoffTicks = DateTime.UtcNow.AddHours(-24).Ticks;
            await db.SortedSetRemoveRangeByScoreAsync(_processingKey, 0, cutoffTicks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup stale queue data", null, null);
        }
    }

    private async Task CleanupCompletedJobsAsync()
    {
        try
        {
            var db = _redis.GetDatabase();

            // Keep completed jobs for 24 hours, then remove
            var cutoffTicks = DateTime.UtcNow.AddHours(-24).Ticks;
            var removed = await db.SortedSetRemoveRangeByScoreAsync(_completedKey, 0, cutoffTicks);

            if (removed > 0)
            {
                _logger.LogDebug(
                    $"Cleaned up {removed} completed harvest jobs older than 24 hours",
                    null,
                    null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup completed jobs", null, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _logger.LogInformation("RedisHarvestQueue disposed", null, null);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Queue statistics DTO
/// </summary>
public class HarvestQueueStats
{
    public int QueuedCount { get; set; }
    public int ProcessingCount { get; set; }
    public int CompletedCount { get; set; }
    public int TotalCount { get; set; }
    public bool IsCompletionRequested { get; set; }
}
