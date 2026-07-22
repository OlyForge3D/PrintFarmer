using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for <see cref="Worker"/> entity operations within the slicer module.
/// </summary>
public interface IWorkerRepository
{
    /// <summary>Adds a new worker.</summary>
    /// <param name="worker">The worker entity to add.</param>
    Task AddAsync(Worker worker);

    /// <summary>Gets a worker by its unique identifier.</summary>
    /// <param name="id">The worker identifier.</param>
    Task<Worker?> GetByIdAsync(Guid id);

    /// <summary>Gets a worker by its service identifier from the registry.</summary>
    /// <param name="serviceId">The service identifier.</param>
    Task<Worker?> GetByServiceIdAsync(string serviceId);

    /// <summary>Gets a worker by its endpoint URL.</summary>
    /// <param name="endpointUrl">The endpoint URL.</param>
    Task<Worker?> GetByEndpointUrlAsync(string endpointUrl);

    /// <summary>Gets all workers with pagination.</summary>
    /// <param name="limit">Maximum number of workers to return.</param>
    /// <param name="offset">Number of workers to skip.</param>
    Task<IReadOnlyList<Worker>> GetAllAsync(int limit = 100, int offset = 0);

    /// <summary>Gets workers filtered by status.</summary>
    /// <param name="status">The status to filter by.</param>
    /// <param name="limit">Maximum number of workers to return.</param>
    /// <param name="offset">Number of workers to skip.</param>
    Task<IReadOnlyList<Worker>> GetByStatusAsync(string status, int limit = 100, int offset = 0);

    /// <summary>Gets online workers with available processing slots.</summary>
    /// <param name="limit">Maximum number of workers to return.</param>
    Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync(int limit = 100);

    /// <summary>Gets workers with the specified capabilities.</summary>
    /// <param name="requiredCapabilities">Required capability names.</param>
    /// <param name="limit">Maximum number of workers to return.</param>
    Task<IReadOnlyList<Worker>> GetWorkersByCapabilitiesAsync(string[] requiredCapabilities, int limit = 100);

    /// <summary>Gets workers that haven't sent a heartbeat within the timeout period.</summary>
    /// <param name="heartbeatTimeout">The timeout duration.</param>
    Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout);

    /// <summary>Updates a worker's status.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="status">The new status.</param>
    Task UpdateStatusAsync(Guid id, string status);

    /// <summary>Updates a worker's heartbeat and slot availability.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="freeSlots">Number of available processing slots.</param>
    /// <param name="totalSlots">Total number of processing slots.</param>
    Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots);

    /// <summary>Increments the active job count for a worker.</summary>
    /// <param name="id">The worker identifier.</param>
    Task IncrementActiveJobsAsync(Guid id);

    /// <summary>Decrements the active job count and records job result metrics.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="success">Whether the job completed successfully.</param>
    /// <param name="processingTimeSeconds">Job processing time in seconds.</param>
    Task DecrementActiveJobsAsync(Guid id, bool success, double processingTimeSeconds);

    /// <summary>Disables a worker with a reason.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="reason">The reason for disabling.</param>
    Task DisableWorkerAsync(Guid id, string reason);

    /// <summary>Re-enables a disabled worker.</summary>
    /// <param name="id">The worker identifier.</param>
    Task EnableWorkerAsync(Guid id);

    /// <summary>Resets a worker's active job count to zero and sets status to Online.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <returns>True if the worker was found and reset; false if not found.</returns>
    Task<bool> ResetAsync(Guid id);

    /// <summary>Deletes a worker.</summary>
    /// <param name="id">The worker identifier.</param>
    Task DeleteAsync(Guid id);

    /// <summary>Updates a worker's total processing slot count.</summary>
    /// <param name="id">The worker identifier.</param>
    /// <param name="totalSlots">The new total slot count.</param>
    Task UpdateTotalSlotsAsync(Guid id, int totalSlots);

    /// <summary>Saves pending changes to the database.</summary>
    Task SaveChangesAsync();
}
