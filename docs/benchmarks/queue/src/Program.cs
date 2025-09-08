using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QueueBenchmark.Providers;
using QueueBenchmark.Models;
using QueueBenchmark.Shared;

namespace QueueBenchmark;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Check for specific benchmark argument
        if (args.Length > 0 && args[0] == "benchmark")
        {
            BenchmarkRunner.Run<QueueProviderBenchmark>();
            return;
        }

        // Run the POC demonstration
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddLogging(builder => builder.AddConsole());
                services.AddTransient<QueueBenchmarkRunner>();
                services.AddTransient<RedisQueueProvider>();
                services.AddTransient<RabbitMqQueueProvider>();
                services.AddTransient<KafkaQueueProvider>();
            })
            .Build();

        var benchmarkRunner = host.Services.GetRequiredService<QueueBenchmarkRunner>();
        await benchmarkRunner.RunPocAsync();
        
        await host.RunAsync();
    }
}

public class QueueBenchmarkRunner
{
    private readonly ILogger<QueueBenchmarkRunner> _logger;
    private readonly RedisQueueProvider _redisProvider;
    private readonly RabbitMqQueueProvider _rabbitMqProvider;
    private readonly KafkaQueueProvider _kafkaProvider;

    public QueueBenchmarkRunner(
        ILogger<QueueBenchmarkRunner> logger,
        RedisQueueProvider redisProvider,
        RabbitMqQueueProvider rabbitMqProvider,
        KafkaQueueProvider kafkaProvider)
    {
        _logger = logger;
        _redisProvider = redisProvider;
        _rabbitMqProvider = rabbitMqProvider;
        _kafkaProvider = kafkaProvider;
    }

    public async Task RunPocAsync()
    {
        _logger.LogInformation("Starting Queue Provider POC with 100 sample jobs");

        var providers = new Dictionary<string, IQueueProvider>
        {
            { "Redis", _redisProvider },
            { "RabbitMQ", _rabbitMqProvider },
            { "Kafka", _kafkaProvider }
        };

        foreach (var (name, provider) in providers)
        {
            _logger.LogInformation("Testing {ProviderName}", name);
            
            try
            {
                await TestProviderAsync(name, provider);
                _logger.LogInformation("{ProviderName} test completed successfully", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{ProviderName} test failed", name);
            }
        }
    }

    private async Task TestProviderAsync(string providerName, IQueueProvider provider)
    {
        const int jobCount = 100;
        var jobs = GenerateTestJobs(jobCount);
        
        // Initialize provider
        await provider.InitializeAsync();

        var stopwatch = Stopwatch.StartNew();

        // Enqueue all jobs
        _logger.LogInformation("Enqueuing {JobCount} jobs to {ProviderName}", jobCount, providerName);
        foreach (var job in jobs)
        {
            await provider.EnqueueAsync(job);
        }
        
        var enqueueTime = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();

        // Dequeue and process all jobs
        _logger.LogInformation("Dequeuing jobs from {ProviderName}", providerName);
        var processedJobs = new List<TestJob>();
        var successCount = 0;
        var retryCount = 0;
        var dlqCount = 0;

        for (int i = 0; i < jobCount; i++)
        {
            var job = await provider.DequeueAsync();
            if (job != null)
            {
                processedJobs.Add(job);
                
                // Simulate processing with some failures for retry/DLQ testing
                if (job.Id % 20 == 0) // 5% failure rate for retry testing
                {
                    await provider.RetryAsync(job);
                    retryCount++;
                }
                else if (job.Id % 50 == 0) // 2% failure rate for DLQ testing  
                {
                    await provider.SendToDeadLetterQueueAsync(job, "Simulated processing failure");
                    dlqCount++;
                }
                else
                {
                    await provider.AcknowledgeAsync(job);
                    successCount++;
                }
            }
        }

        var dequeueTime = stopwatch.ElapsedMilliseconds;

        // Log results
        _logger.LogInformation(
            "{ProviderName} Results: Enqueue={EnqueueTime}ms, Dequeue={DequeueTime}ms, Success={Success}, Retry={Retry}, DLQ={DLQ}",
            providerName, enqueueTime, dequeueTime, successCount, retryCount, dlqCount);

        // Cleanup
        await provider.CleanupAsync();
    }

    private static List<TestJob> GenerateTestJobs(int count)
    {
        var jobs = new List<TestJob>();
        var random = new Random(42); // Fixed seed for reproducibility

        for (int i = 1; i <= count; i++)
        {
            var priority = (SlicingJobPriority)random.Next(0, 4);
            var engine = (SlicerEngineType)random.Next(0, 3);
            var payloadSize = random.Next(1024, 51200); // 1KB to 50KB
            
            jobs.Add(new TestJob
            {
                Id = i,
                Priority = priority,
                Engine = engine,
                Payload = GeneratePayload(payloadSize),
                CreatedAt = DateTime.UtcNow.AddSeconds(-random.Next(0, 300)) // Jobs created in last 5 minutes
            });
        }

        return jobs;
    }

    private static string GeneratePayload(int sizeBytes)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, sizeBytes)
            .Select(s => s[random.Next(s.Length)])
            .ToArray());
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class QueueProviderBenchmark
{
    private RedisQueueProvider? _redisProvider;
    private RabbitMqQueueProvider? _rabbitMqProvider;
    private KafkaQueueProvider? _kafkaProvider;
    private List<TestJob> _smallJobs = new();
    private List<TestJob> _mediumJobs = new();
    private List<TestJob> _largeJobs = new();

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        
        _redisProvider = new RedisQueueProvider(loggerFactory.CreateLogger<RedisQueueProvider>());
        _rabbitMqProvider = new RabbitMqQueueProvider(loggerFactory.CreateLogger<RabbitMqQueueProvider>());
        _kafkaProvider = new KafkaQueueProvider(loggerFactory.CreateLogger<KafkaQueueProvider>());

        await _redisProvider.InitializeAsync();
        await _rabbitMqProvider.InitializeAsync();
        await _kafkaProvider.InitializeAsync();

        // Generate test data
        _smallJobs = GenerateJobs(10, 1024);        // 10 jobs, 1KB each
        _mediumJobs = GenerateJobs(100, 10240);     // 100 jobs, 10KB each
        _largeJobs = GenerateJobs(1000, 102400);    // 1000 jobs, 100KB each
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_redisProvider != null) await _redisProvider.CleanupAsync();
        if (_rabbitMqProvider != null) await _rabbitMqProvider.CleanupAsync();
        if (_kafkaProvider != null) await _kafkaProvider.CleanupAsync();
    }

    [Benchmark]
    [Arguments("Small")]
    [Arguments("Medium")]  
    [Arguments("Large")]
    public async Task RedisEnqueueAsync(string loadType)
    {
        var jobs = GetJobsForLoad(loadType);
        foreach (var job in jobs)
        {
            await _redisProvider!.EnqueueAsync(job);
        }
    }

    [Benchmark]
    [Arguments("Small")]
    [Arguments("Medium")]
    [Arguments("Large")] 
    public async Task RabbitMqEnqueueAsync(string loadType)
    {
        var jobs = GetJobsForLoad(loadType);
        foreach (var job in jobs)
        {
            await _rabbitMqProvider!.EnqueueAsync(job);
        }
    }

    [Benchmark]
    [Arguments("Small")]
    [Arguments("Medium")]
    [Arguments("Large")]
    public async Task KafkaEnqueueAsync(string loadType)
    {
        var jobs = GetJobsForLoad(loadType);
        foreach (var job in jobs)
        {
            await _kafkaProvider!.EnqueueAsync(job);
        }
    }

    private List<TestJob> GetJobsForLoad(string loadType) => loadType switch
    {
        "Small" => _smallJobs,
        "Medium" => _mediumJobs,
        "Large" => _largeJobs,
        _ => _smallJobs
    };

    private static List<TestJob> GenerateJobs(int count, int payloadSize)
    {
        var jobs = new List<TestJob>();
        var random = new Random(42);

        for (int i = 1; i <= count; i++)
        {
            jobs.Add(new TestJob
            {
                Id = i,
                Priority = (SlicingJobPriority)random.Next(0, 4),
                Engine = (SlicerEngineType)random.Next(0, 3),
                Payload = new string('x', payloadSize),
                CreatedAt = DateTime.UtcNow
            });
        }

        return jobs;
    }
}