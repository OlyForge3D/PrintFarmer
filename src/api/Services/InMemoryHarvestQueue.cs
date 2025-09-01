using System.Threading.Channels;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Models;

namespace Farm.Web.Api.Services;

/// <summary>
/// In-memory implementation of harvest file processing queue using System.Threading.Channels
/// </summary>
public class InMemoryHarvestQueue : IHarvestQueue, IDisposable
{
    private readonly Channel<HarvestFileJob> _channel;
    private readonly ILogger<InMemoryHarvestQueue> _logger;
    private bool _disposed = false;

    public InMemoryHarvestQueue(ILogger<InMemoryHarvestQueue> logger)
    {
        _logger = logger;
        
        // Create unbounded channel for maximum throughput
        // In production, you might want bounded channels with backpressure
        var options = new UnboundedChannelOptions
        {
            SingleReader = false, // Allow multiple workers
            SingleWriter = false, // Allow multiple harvest operations
            AllowSynchronousContinuations = false // Better performance
        };
        
        _channel = Channel.CreateUnbounded<HarvestFileJob>(options);
        
        _logger.LogInformation("InMemoryHarvestQueue initialized");
    }

    public async Task EnqueueAsync(HarvestFileJob job, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InMemoryHarvestQueue));
            
        try
        {
            await _channel.Writer.WriteAsync(job, ct);
            _logger.LogDebug("Enqueued job for file {FileName} from operation {OperationId}", 
                job.FileName, job.OperationId);
        }
        catch (InvalidOperationException)
        {
            // Channel was completed
            _logger.LogWarning("Attempted to enqueue job {JobFileName} but queue is completed", job.FileName);
            throw;
        }
    }

    public async IAsyncEnumerable<HarvestFileJob> DequeueAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed)
            yield break;
            
        await foreach (var job in _channel.Reader.ReadAllAsync(ct))
        {
            _logger.LogDebug("Dequeued job for file {FileName} from operation {OperationId}", 
                job.FileName, job.OperationId);
            yield return job;
        }
        
        _logger.LogInformation("Queue reading completed - no more jobs available");
    }

    public int QueueDepth
    {
        get
        {
            if (_disposed)
                return 0;
                
            // Note: Channel doesn't provide exact count in .NET
            // This is a simplified implementation
            return 0; // Would need custom implementation for accurate count
        }
    }

    public void CompleteAdding()
    {
        if (_disposed)
            return;
            
        _channel.Writer.Complete();
        _logger.LogInformation("Harvest queue marked as complete - no more jobs will be accepted");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
            
        _disposed = true;
        
        // Complete the channel if not already done
        _channel.Writer.Complete();
        
        _logger.LogInformation("InMemoryHarvestQueue disposed");
    }
}
