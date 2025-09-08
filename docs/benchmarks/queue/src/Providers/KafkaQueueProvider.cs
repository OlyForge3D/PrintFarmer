using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using QueueBenchmark.Models;

namespace QueueBenchmark.Providers;

/// <summary>
/// Kafka-based queue provider for benchmarking
/// Note: Kafka doesn't have native priority queues or DLQ, so we simulate these features
/// </summary>
public class KafkaQueueProvider : IQueueProvider
{
    private readonly ILogger<KafkaQueueProvider> _logger;
    private IProducer<string, string>? _producer;
    private IConsumer<string, string>? _consumer;
    
    private const string TopicName = "benchmark-jobs";
    private const string RetryTopicName = "benchmark-jobs-retry";
    private const string DeadLetterTopicName = "benchmark-jobs-dlq";
    private const string ConsumerGroup = "benchmark-consumer-group";

    public string Name => "Kafka";

    public KafkaQueueProvider(ILogger<KafkaQueueProvider> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = "localhost:9092",
                Acks = Acks.All,
                RetryBackoffMs = 1000,
                MessageTimeoutMs = 30000,
            };

            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = "localhost:9092",
                GroupId = ConsumerGroup,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            };

            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
            _consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();

            // Subscribe to topics
            _consumer.Subscribe(new[] { TopicName, RetryTopicName });

            // Create topics if they don't exist (in production, this should be done externally)
            await CreateTopicsIfNeededAsync();

            _logger.LogInformation("Kafka queue provider initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Kafka queue provider");
            throw;
        }
    }

    public async Task EnqueueAsync(TestJob job)
    {
        if (_producer == null) throw new InvalidOperationException("Provider not initialized");

        var message = JsonSerializer.Serialize(job);
        var headers = new Headers
        {
            { "priority", BitConverter.GetBytes((int)job.Priority) },
            { "engine", System.Text.Encoding.UTF8.GetBytes(job.Engine.ToString()) },
            { "created_at", System.Text.Encoding.UTF8.GetBytes(job.CreatedAt.ToString("O")) }
        };

        try
        {
            await _producer.ProduceAsync(TopicName, new Message<string, string>
            {
                Key = job.Id.ToString(),
                Value = message,
                Headers = headers,
                Timestamp = new Timestamp(job.CreatedAt)
            });
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to produce message to Kafka");
            throw;
        }
    }

    public async Task<TestJob?> DequeueAsync()
    {
        if (_consumer == null) throw new InvalidOperationException("Provider not initialized");

        try
        {
            var consumeResult = _consumer.Consume(TimeSpan.FromSeconds(1));
            if (consumeResult == null) return null;

            var job = JsonSerializer.Deserialize<TestJob>(consumeResult.Message.Value);
            if (job != null)
            {
                // Store Kafka-specific metadata for later acknowledgment
                job.SetKafkaMetadata(consumeResult.TopicPartitionOffset);
            }

            return job;
        }
        catch (ConsumeException ex)
        {
            _logger.LogError(ex, "Failed to consume message from Kafka");
            return null;
        }
    }

    public async Task AcknowledgeAsync(TestJob job)
    {
        if (_consumer == null) throw new InvalidOperationException("Provider not initialized");

        var metadata = job.GetKafkaMetadata();
        if (metadata.HasValue)
        {
            _consumer.Commit(new List<TopicPartitionOffset> { metadata.Value });
        }

        await Task.CompletedTask; // Kafka commit is synchronous
    }

    public async Task RetryAsync(TestJob job)
    {
        if (_producer == null) throw new InvalidOperationException("Provider not initialized");

        job.RetryCount++;

        if (job.RetryCount >= 3)
        {
            await SendToDeadLetterQueueAsync(job, $"Max retry attempts exceeded ({job.RetryCount})");
            await AcknowledgeAsync(job); // Acknowledge to remove from original topic
            return;
        }

        // Send to retry topic with delay (simulated by separate topic)
        var message = JsonSerializer.Serialize(job);
        var headers = new Headers
        {
            { "retry_count", BitConverter.GetBytes(job.RetryCount) },
            { "original_topic", System.Text.Encoding.UTF8.GetBytes(TopicName) },
            { "retry_reason", System.Text.Encoding.UTF8.GetBytes("Retry requested") }
        };

        await _producer.ProduceAsync(RetryTopicName, new Message<string, string>
        {
            Key = job.Id.ToString(),
            Value = message,
            Headers = headers,
            Timestamp = new Timestamp(DateTime.UtcNow.AddSeconds(30)) // Simulate delay
        });

        // Acknowledge original message
        await AcknowledgeAsync(job);
    }

    public async Task SendToDeadLetterQueueAsync(TestJob job, string reason)
    {
        if (_producer == null) throw new InvalidOperationException("Provider not initialized");

        job.ErrorMessage = reason;
        job.ProcessedAt = DateTime.UtcNow;

        var message = JsonSerializer.Serialize(job);
        var headers = new Headers
        {
            { "error_reason", System.Text.Encoding.UTF8.GetBytes(reason) },
            { "failed_at", System.Text.Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")) },
            { "original_topic", System.Text.Encoding.UTF8.GetBytes(TopicName) }
        };

        await _producer.ProduceAsync(DeadLetterTopicName, new Message<string, string>
        {
            Key = job.Id.ToString(),
            Value = message,
            Headers = headers,
            Timestamp = new Timestamp(DateTime.UtcNow)
        });
    }

    public async Task<QueueMetrics> GetMetricsAsync()
    {
        // Kafka doesn't provide direct queue depth metrics like Redis or RabbitMQ
        // In production, you'd use Kafka management tools or JMX metrics
        // For benchmarking, we return estimated values
        return new QueueMetrics
        {
            QueueDepth = 0, // Would need external monitoring
            ProcessingCount = 0, // Kafka doesn't track this
            CompletedCount = 0, // Would need separate tracking
            FailedCount = 0, // Would need separate tracking
            DeadLetterCount = 0, // Would need topic inspection
            LastUpdate = DateTime.UtcNow
        };
    }

    public async Task CleanupAsync()
    {
        if (_consumer != null)
        {
            _consumer.Close();
            _consumer.Dispose();
            _consumer = null;
        }
        
        if (_producer != null)
        {
            _producer.Flush();
            _producer.Dispose();
            _producer = null;
        }
        
        _logger.LogInformation("Kafka queue provider cleaned up");
        await Task.CompletedTask;
    }

    private async Task CreateTopicsIfNeededAsync()
    {
        // In a real implementation, you'd use AdminClient to create topics
        // For this benchmark, we assume topics exist or are auto-created
        _logger.LogInformation("Topics assumed to exist or will be auto-created");
        await Task.CompletedTask;
    }
}

// Extension methods to store Kafka-specific metadata
public static class TestJobKafkaExtensions
{
    private static readonly Dictionary<TestJob, TopicPartitionOffset> _kafkaMetadata = new();
    
    public static TopicPartitionOffset? GetKafkaMetadata(this TestJob job)
    {
        return _kafkaMetadata.TryGetValue(job, out var metadata) ? metadata : (TopicPartitionOffset?)null;
    }
    
    public static void SetKafkaMetadata(this TestJob job, TopicPartitionOffset metadata)
    {
        _kafkaMetadata[job] = metadata;
    }
}