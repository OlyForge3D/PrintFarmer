using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Repositories.Workers;

/// <summary>
/// Repository interface for Worker entity operations
/// </summary>
public interface IWorkerRepository
{
    /// <summary>
    /// Add a new worker
    /// </summary>
    Task AddAsync(Worker worker);

    /// <summary>
    /// Get worker by ID
    /// </summary>
    Task<Worker?> GetByIdAsync(Guid id);

    /// <summary>
    /// Get worker by service ID (from registry)
    /// </summary>
    Task<Worker?> GetByServiceIdAsync(string serviceId);

    /// <summary>
    /// Get all workers
    /// </summary>
    Task<IReadOnlyList<Worker>> GetAllAsync(int limit = 100, int offset = 0);

    /// <summary>
    /// Get workers by status
    /// </summary>
    Task<IReadOnlyList<Worker>> GetByStatusAsync(string status, int limit = 100, int offset = 0);

    /// <summary>
    /// Get online workers with available slots
    /// </summary>
    Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync(int limit = 100);

    /// <summary>
    /// Get workers with specific capabilities
    /// </summary>
    Task<IReadOnlyList<Worker>> GetWorkersByCapabilitiesAsync(string[] requiredCapabilities, int limit = 100);

    /// <summary>
    /// Get workers that haven't sent heartbeat within timeout period
    /// </summary>
    Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout);

    /// <summary>
    /// Update worker status
    /// </summary>
    Task UpdateStatusAsync(Guid id, string status);

    /// <summary>
    /// Update worker heartbeat and availability
    /// </summary>
    Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots);

    /// <summary>
    /// Increment active job count for a worker
    /// </summary>
    Task IncrementActiveJobsAsync(Guid id);

    /// <summary>
    /// Decrement active job count and record job result
    /// </summary>
    Task DecrementActiveJobsAsync(Guid id, bool success, double processingTimeSeconds);

    /// <summary>
    /// Disable a worker
    /// </summary>
    Task DisableWorkerAsync(Guid id, string reason);

    /// <summary>
    /// Enable a worker
    /// </summary>
    Task EnableWorkerAsync(Guid id);

    /// <summary>
    /// Delete a worker
    /// </summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Save changes to database
    /// </summary>
    Task SaveChangesAsync();
}
