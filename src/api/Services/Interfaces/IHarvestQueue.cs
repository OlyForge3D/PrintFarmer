using Farm.Web.Api.Services.Models;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Queue for managing G-code harvest file processing jobs
/// </summary>
public interface IHarvestQueue
{
    /// <summary>
    /// Add a file processing job to the queue
    /// </summary>
    Task EnqueueAsync(HarvestFileJob job, CancellationToken ct = default);

    /// <summary>
    /// Get jobs from the queue for processing
    /// </summary>
    IAsyncEnumerable<HarvestFileJob> DequeueAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the number of jobs waiting in the queue
    /// </summary>
    int QueueDepth { get; }

    /// <summary>
    /// Complete the queue (no more jobs will be added)
    /// </summary>
    void CompleteAdding();
}
