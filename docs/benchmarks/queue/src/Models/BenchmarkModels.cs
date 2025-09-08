using QueueBenchmark.Shared;

namespace QueueBenchmark.Models;

/// <summary>
/// Simplified test job for benchmarking
/// </summary>
public class TestJob
{
    public int Id { get; set; }
    public SlicingJobPriority Priority { get; set; }
    public SlicerEngineType Engine { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; } = 0;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Benchmark configuration
/// </summary>
public class BenchmarkConfig
{
    public string RedisConnectionString { get; set; } = "localhost:6379";
    public string RabbitMqConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";
    public string KafkaBootstrapServers { get; set; } = "localhost:9092";
    
    public int SmallLoadJobCount { get; set; } = 10;
    public int MediumLoadJobCount { get; set; } = 100;
    public int LargeLoadJobCount { get; set; } = 1000;
    
    public int SmallPayloadSize { get; set; } = 1024;      // 1KB
    public int MediumPayloadSize { get; set; } = 10240;    // 10KB
    public int LargePayloadSize { get; set; } = 102400;    // 100KB
}

/// <summary>
/// Benchmark results for a single test run
/// </summary>
public class BenchmarkResult
{
    public string ProviderName { get; set; } = string.Empty;
    public string TestScenario { get; set; } = string.Empty;
    public DateTime TestStartTime { get; set; }
    public DateTime TestEndTime { get; set; }
    
    public int JobCount { get; set; }
    public long EnqueueTimeMs { get; set; }
    public long DequeueTimeMs { get; set; }
    public long TotalTimeMs { get; set; }
    
    public double EnqueueThroughputJobsPerSecond { get; set; }
    public double DequeueThroughputJobsPerSecond { get; set; }
    public double OverallThroughputJobsPerSecond { get; set; }
    
    public double AverageEnqueueLatencyMs { get; set; }
    public double AverageDequeueLatencyMs { get; set; }
    public double P95EnqueueLatencyMs { get; set; }
    public double P95DequeueLatencyMs { get; set; }
    public double P99EnqueueLatencyMs { get; set; }
    public double P99DequeueLatencyMs { get; set; }
    
    public int SuccessfulJobs { get; set; }
    public int RetryJobs { get; set; }
    public int DeadLetterJobs { get; set; }
    public int FailedJobs { get; set; }
    
    public long PeakMemoryUsageBytes { get; set; }
    public double AverageCpuUsagePercent { get; set; }
}

/// <summary>
/// Queue provider interface for benchmarking
/// </summary>
public interface IQueueProvider
{
    string Name { get; }
    Task InitializeAsync();
    Task EnqueueAsync(TestJob job);
    Task<TestJob?> DequeueAsync();
    Task AcknowledgeAsync(TestJob job);
    Task RetryAsync(TestJob job);
    Task SendToDeadLetterQueueAsync(TestJob job, string reason);
    Task<QueueMetrics> GetMetricsAsync();
    Task CleanupAsync();
}

/// <summary>
/// Queue metrics for monitoring
/// </summary>
public class QueueMetrics
{
    public int QueueDepth { get; set; }
    public int ProcessingCount { get; set; }
    public int CompletedCount { get; set; }
    public int FailedCount { get; set; }
    public int DeadLetterCount { get; set; }
    public DateTime LastUpdate { get; set; }
}