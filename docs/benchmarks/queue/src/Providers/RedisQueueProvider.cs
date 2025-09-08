using System.Text.Json;
using Microsoft.Extensions.Logging;
using QueueBenchmark.Models;
using StackExchange.Redis;

namespace QueueBenchmark.Providers;

/// <summary>
/// Redis-based queue provider for benchmarking
/// Uses Redis sorted sets for priority queuing similar to the production implementation
/// </summary>
public class RedisQueueProvider : IQueueProvider
{
    private readonly ILogger<RedisQueueProvider> _logger;
    private ConnectionMultiplexer? _redis;
    private IDatabase? _database;
    
    private const string QueueKey = "benchmark:queue";
    private const string ProcessingKey = "benchmark:processing";
    private const string CompletedKey = "benchmark:completed";
    private const string FailedKey = "benchmark:failed";
    private const string DeadLetterKey = "benchmark:dlq";

    public string Name => "Redis";

    public RedisQueueProvider(ILogger<RedisQueueProvider> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            _database = _redis.GetDatabase();
            
            // Clean up any existing benchmark data
            await _database.KeyDeleteAsync(new RedisKey[] 
            { 
                QueueKey, ProcessingKey, CompletedKey, FailedKey, DeadLetterKey 
            });
            
            _logger.LogInformation("Redis queue provider initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Redis queue provider");
            throw;
        }
    }

    public async Task EnqueueAsync(TestJob job)
    {
        if (_database == null) throw new InvalidOperationException("Provider not initialized");

        var jobJson = JsonSerializer.Serialize(job);
        var score = GetPriorityScore(job.Priority, job.CreatedAt);
        
        await _database.SortedSetAddAsync(QueueKey, jobJson, score);
    }

    public async Task<TestJob?> DequeueAsync()
    {
        if (_database == null) throw new InvalidOperationException("Provider not initialized");

        var jobData = await _database.SortedSetPopAsync(QueueKey, Order.Ascending);
        if (!jobData.HasValue) return null;

        var job = JsonSerializer.Deserialize<TestJob>(jobData.Value.Element!);
        if (job == null) return null;

        // Move to processing set
        var processingJson = JsonSerializer.Serialize(job);
        await _database.SortedSetAddAsync(ProcessingKey, processingJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        
        return job;
    }

    public async Task AcknowledgeAsync(TestJob job)
    {
        if (_database == null) throw new InvalidOperationException("Provider not initialized");

        job.ProcessedAt = DateTime.UtcNow;
        
        // Remove from processing and add to completed
        var processingJson = JsonSerializer.Serialize(job);
        await _database.SortedSetRemoveAsync(ProcessingKey, processingJson);
        
        var completedJson = JsonSerializer.Serialize(job);
        await _database.SortedSetAddAsync(CompletedKey, completedJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public async Task RetryAsync(TestJob job)
    {
        if (_database == null) throw new InvalidOperationException("Provider not initialized");

        job.RetryCount++;
        
        if (job.RetryCount >= 3)
        {
            await SendToDeadLetterQueueAsync(job, $"Max retry attempts exceeded ({job.RetryCount})");
            return;
        }

        // Remove from processing and re-queue with lower priority
        var processingJson = JsonSerializer.Serialize(job);
        await _database.SortedSetRemoveAsync(ProcessingKey, processingJson);
        
        // Re-queue with penalty (higher score = lower priority)
        var retryScore = GetPriorityScore(job.Priority, job.CreatedAt) + (job.RetryCount * 1000);
        var requeueJson = JsonSerializer.Serialize(job);
        await _database.SortedSetAddAsync(QueueKey, requeueJson, retryScore);
    }

    public async Task SendToDeadLetterQueueAsync(TestJob job, string reason)
    {
        if (_database == null) throw new InvalidOperationException("Provider not initialized");

        job.ErrorMessage = reason;
        job.ProcessedAt = DateTime.UtcNow;
        
        // Remove from processing if present
        var processingJson = JsonSerializer.Serialize(job);
        await _database.SortedSetRemoveAsync(ProcessingKey, processingJson);
        
        // Add to dead letter queue
        var dlqJson = JsonSerializer.Serialize(job);
        await _database.SortedSetAddAsync(DeadLetterKey, dlqJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public async Task<QueueMetrics> GetMetricsAsync()
    {
        if (_database == null) throw new InvalidOperationException("Provider not initialized");

        return new QueueMetrics
        {
            QueueDepth = (int)await _database.SortedSetLengthAsync(QueueKey),
            ProcessingCount = (int)await _database.SortedSetLengthAsync(ProcessingKey),
            CompletedCount = (int)await _database.SortedSetLengthAsync(CompletedKey),
            FailedCount = (int)await _database.SortedSetLengthAsync(FailedKey),
            DeadLetterCount = (int)await _database.SortedSetLengthAsync(DeadLetterKey),
            LastUpdate = DateTime.UtcNow
        };
    }

    public async Task CleanupAsync()
    {
        if (_database != null)
        {
            await _database.KeyDeleteAsync(new RedisKey[] 
            { 
                QueueKey, ProcessingKey, CompletedKey, FailedKey, DeadLetterKey 
            });
        }
        
        if (_redis != null)
        {
            await _redis.DisposeAsync();
            _redis = null;
            _database = null;
        }
        
        _logger.LogInformation("Redis queue provider cleaned up");
    }

    private static double GetPriorityScore(QueueBenchmark.Shared.SlicingJobPriority priority, DateTime createdAt)
    {
        // Higher numeric priority = lower score (higher Redis priority)
        var priorityWeight = (4 - (int)priority) * 1000000;
        
        // Older jobs get lower score (higher priority)
        var timeWeight = (DateTimeOffset.MaxValue.Ticks - createdAt.Ticks) / 1000000.0;
        
        return priorityWeight + timeWeight;
    }
}