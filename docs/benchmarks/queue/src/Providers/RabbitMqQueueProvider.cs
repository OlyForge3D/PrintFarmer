using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QueueBenchmark.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace QueueBenchmark.Providers;

/// <summary>
/// RabbitMQ-based queue provider for benchmarking
/// Implements priority queues, retries, and dead letter queues
/// </summary>
public class RabbitMqQueueProvider : IQueueProvider
{
    private readonly ILogger<RabbitMqQueueProvider> _logger;
    private IConnection? _connection;
    private IModel? _channel;
    
    private const string QueueName = "benchmark_queue";
    private const string ExchangeName = "benchmark_exchange";
    private const string DeadLetterExchangeName = "benchmark_dlx";
    private const string DeadLetterQueueName = "benchmark_dlq";
    private const string RetryExchangeName = "benchmark_retry_exchange";
    private const string RetryQueueName = "benchmark_retry_queue";

    public string Name => "RabbitMQ";

    public RabbitMqQueueProvider(ILogger<RabbitMqQueueProvider> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = "localhost",
                UserName = "guest",
                Password = "guest",
                Port = 5672,
                VirtualHost = "/"
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // Declare exchanges
            _channel.ExchangeDeclare(ExchangeName, ExchangeType.Direct, durable: true);
            _channel.ExchangeDeclare(DeadLetterExchangeName, ExchangeType.Direct, durable: true);
            _channel.ExchangeDeclare(RetryExchangeName, ExchangeType.Direct, durable: true);

            // Declare main queue with priority support and DLX
            var queueArgs = new Dictionary<string, object>
            {
                { "x-max-priority", 4 }, // Support priority levels 0-4
                { "x-dead-letter-exchange", DeadLetterExchangeName },
                { "x-dead-letter-routing-key", "failed" }
            };
            _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false, queueArgs);
            _channel.QueueBind(QueueName, ExchangeName, "job");

            // Declare dead letter queue
            _channel.QueueDeclare(DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueBind(DeadLetterQueueName, DeadLetterExchangeName, "failed");

            // Declare retry queue with TTL
            var retryQueueArgs = new Dictionary<string, object>
            {
                { "x-message-ttl", 30000 }, // 30 second delay
                { "x-dead-letter-exchange", ExchangeName },
                { "x-dead-letter-routing-key", "job" }
            };
            _channel.QueueDeclare(RetryQueueName, durable: true, exclusive: false, autoDelete: false, retryQueueArgs);
            _channel.QueueBind(RetryQueueName, RetryExchangeName, "retry");

            // Purge existing messages for clean benchmark
            _channel.QueuePurge(QueueName);
            _channel.QueuePurge(DeadLetterQueueName);
            _channel.QueuePurge(RetryQueueName);

            _logger.LogInformation("RabbitMQ queue provider initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize RabbitMQ queue provider");
            throw;
        }
    }

    public async Task EnqueueAsync(TestJob job)
    {
        if (_channel == null) throw new InvalidOperationException("Provider not initialized");

        var message = JsonSerializer.Serialize(job);
        var body = Encoding.UTF8.GetBytes(message);
        
        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.Priority = GetRabbitMqPriority(job.Priority);
        properties.MessageId = job.Id.ToString();
        properties.Timestamp = new AmqpTimestamp(((DateTimeOffset)job.CreatedAt).ToUnixTimeSeconds());

        _channel.BasicPublish(ExchangeName, "job", properties, body);
        await Task.CompletedTask;
    }

    public async Task<TestJob?> DequeueAsync()
    {
        if (_channel == null) throw new InvalidOperationException("Provider not initialized");

        var result = _channel.BasicGet(QueueName, autoAck: false);
        if (result == null) return null;

        var message = Encoding.UTF8.GetString(result.Body.ToArray());
        var job = JsonSerializer.Deserialize<TestJob>(message);
        
        if (job != null)
        {
            // Store delivery tag for acknowledgment
            job.SetDeliveryTag(result.DeliveryTag);
        }

        return job;
    }

    public async Task AcknowledgeAsync(TestJob job)
    {
        if (_channel == null) throw new InvalidOperationException("Provider not initialized");

        _channel.BasicAck(job.GetDeliveryTag(), multiple: false);
        await Task.CompletedTask;
    }

    public async Task RetryAsync(TestJob job)
    {
        if (_channel == null) throw new InvalidOperationException("Provider not initialized");

        job.RetryCount++;

        if (job.RetryCount >= 3)
        {
            await SendToDeadLetterQueueAsync(job, $"Max retry attempts exceeded ({job.RetryCount})");
            _channel.BasicAck(job.GetDeliveryTag(), multiple: false);
            return;
        }

        // Reject message to retry queue
        _channel.BasicReject(job.GetDeliveryTag(), requeue: false);
        
        // Publish to retry exchange (will be delayed by TTL)
        var message = JsonSerializer.Serialize(job);
        var body = Encoding.UTF8.GetBytes(message);
        
        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = job.Id.ToString();

        _channel.BasicPublish(RetryExchangeName, "retry", properties, body);
    }

    public async Task SendToDeadLetterQueueAsync(TestJob job, string reason)
    {
        if (_channel == null) throw new InvalidOperationException("Provider not initialized");

        job.ErrorMessage = reason;
        job.ProcessedAt = DateTime.UtcNow;

        var message = JsonSerializer.Serialize(job);
        var body = Encoding.UTF8.GetBytes(message);
        
        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = job.Id.ToString();
        properties.Headers = new Dictionary<string, object>
        {
            { "error-reason", reason },
            { "failed-at", DateTime.UtcNow.ToString("O") }
        };

        _channel.BasicPublish(DeadLetterExchangeName, "failed", properties, body);
        
        // Acknowledge original message
        if (job.GetDeliveryTag() > 0)
        {
            _channel.BasicAck(job.GetDeliveryTag(), multiple: false);
        }
    }

    public async Task<QueueMetrics> GetMetricsAsync()
    {
        if (_channel == null) throw new InvalidOperationException("Provider not initialized");

        var queueInfo = _channel.QueueDeclarePassive(QueueName);
        var dlqInfo = _channel.QueueDeclarePassive(DeadLetterQueueName);
        var retryInfo = _channel.QueueDeclarePassive(RetryQueueName);

        return new QueueMetrics
        {
            QueueDepth = (int)queueInfo.MessageCount,
            ProcessingCount = 0, // RabbitMQ doesn't track this directly
            CompletedCount = 0, // Would need separate tracking
            FailedCount = 0, // Would need separate tracking
            DeadLetterCount = (int)dlqInfo.MessageCount,
            LastUpdate = DateTime.UtcNow
        };
    }

    public async Task CleanupAsync()
    {
        if (_channel != null)
        {
            try
            {
                // Purge queues
                _channel.QueuePurge(QueueName);
                _channel.QueuePurge(DeadLetterQueueName);
                _channel.QueuePurge(RetryQueueName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error purging RabbitMQ queues during cleanup");
            }

            _channel.Close();
            _channel.Dispose();
            _channel = null;
        }
        
        if (_connection != null)
        {
            _connection.Close();
            _connection.Dispose();
            _connection = null;
        }
        
        _logger.LogInformation("RabbitMQ queue provider cleaned up");
        await Task.CompletedTask;
    }

    private static byte GetRabbitMqPriority(QueueBenchmark.Shared.SlicingJobPriority priority)
    {
        return priority switch
        {
            QueueBenchmark.Shared.SlicingJobPriority.Critical => 4,
            QueueBenchmark.Shared.SlicingJobPriority.High => 3,
            QueueBenchmark.Shared.SlicingJobPriority.Normal => 2,
            QueueBenchmark.Shared.SlicingJobPriority.Low => 1,
            _ => 1
        };
    }
}

// Extension to store RabbitMQ delivery tag
public static class TestJobExtensions
{
    private static readonly Dictionary<TestJob, ulong> _deliveryTags = new();
    
    public static ulong GetDeliveryTag(this TestJob job)
    {
        return _deliveryTags.TryGetValue(job, out var tag) ? tag : 0;
    }
    
    public static void SetDeliveryTag(this TestJob job, ulong deliveryTag)
    {
        _deliveryTags[job] = deliveryTag;
    }
}