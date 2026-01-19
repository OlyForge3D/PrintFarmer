using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Workers;

/// <summary>
/// Repository interface for Worker entity operations
/// </summary>
public interface IWorkerRepository
{
    /// <summary>
    /// Add a new worker
    /// </summary>
    /// <param name="worker">The worker entity to add.</param>
    Task AddAsync(Worker worker);

    /// <summary>
    /// Get worker by ID
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    Task<Worker?> GetByIdAsync(Guid id);

    /// <summary>
    /// Get worker by service ID (from registry)
    /// </summary>
    /// <param name="serviceId">The service identifier from the registry.</param>
    Task<Worker?> GetByServiceIdAsync(string serviceId);

    /// <summary>
    /// Get all workers
    /// </summary>
    /// <param name="limit">The maximum number of workers to return.</param>
    /// <param name="offset">The number of workers to skip.</param>
    Task<IReadOnlyList<Worker>> GetAllAsync(int limit = 100, int offset = 0);

    /// <summary>
    /// Get workers by status
    /// </summary>
    /// <param name="status">The status to filter workers by.</param>
    /// <param name="limit">The maximum number of workers to return.</param>
    /// <param name="offset">The number of workers to skip.</param>
    Task<IReadOnlyList<Worker>> GetByStatusAsync(string status, int limit = 100, int offset = 0);

    /// <summary>
    /// Get online workers with available slots
    /// </summary>
    /// <param name="limit">The maximum number of workers to return.</param>
    Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync(int limit = 100);

    /// <summary>
    /// Get workers with specific capabilities
    /// </summary>
    /// <param name="requiredCapabilities">The array of required capability names.</param>
    /// <param name="limit">The maximum number of workers to return.</param>
    Task<IReadOnlyList<Worker>> GetWorkersByCapabilitiesAsync(string[] requiredCapabilities, int limit = 100);

    /// <summary>
    /// Get workers that haven't sent heartbeat within timeout period
    /// </summary>
    /// <param name="heartbeatTimeout">The timeout duration for considering a worker stale.</param>
    Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout);

    /// <summary>
    /// Update worker status
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    /// <param name="status">The new status to set.</param>
    Task UpdateStatusAsync(Guid id, string status);

    /// <summary>
    /// Update worker heartbeat and availability
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    /// <param name="freeSlots">The number of available processing slots.</param>
    /// <param name="totalSlots">The total number of processing slots.</param>
    Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots);

    /// <summary>
    /// Increment active job count for a worker
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    Task IncrementActiveJobsAsync(Guid id);

    /// <summary>
    /// Decrement active job count and record job result
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    /// <param name="success">Indicates whether the job completed successfully.</param>
    /// <param name="processingTimeSeconds">The job processing time in seconds.</param>
    Task DecrementActiveJobsAsync(Guid id, bool success, double processingTimeSeconds);

    /// <summary>
    /// Disable a worker
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    /// <param name="reason">The reason for disabling the worker.</param>
    Task DisableWorkerAsync(Guid id, string reason);

    /// <summary>
    /// Enable a worker
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    Task EnableWorkerAsync(Guid id);

    /// <summary>
    /// Delete a worker
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Update worker total slots
    /// </summary>
    /// <param name="id">The unique identifier of the worker.</param>
    /// <param name="totalSlots">The new total number of processing slots.</param>
    Task UpdateTotalSlotsAsync(Guid id, int totalSlots);

    /// <summary>
    /// Save changes to database
    /// </summary>
    Task SaveChangesAsync();
}
