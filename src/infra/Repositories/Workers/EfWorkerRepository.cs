using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Workers;

/// <summary>
/// EF Core implementation of IWorkerRepository
/// </summary>
public class EfWorkerRepository : IWorkerRepository
{
    private readonly AppDbContext _context;

    public EfWorkerRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task AddAsync(Worker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _ = await _context.Workers.AddAsync(worker);
    }

    public async Task<Worker?> GetByIdAsync(Guid id)
    {
        // Return tracked entity (needed for update scenarios such as heartbeat/status changes)
        // Callers that need a read-only instance should project or detach manually.
        return await _context.Workers.FirstOrDefaultAsync(w => w.Id == id);
    }

    public async Task<Worker?> GetByServiceIdAsync(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        // Return tracked entity so callers (e.g., SlicersService heartbeat sync) can mutate and persist.
        return await _context.Workers.FirstOrDefaultAsync(w => w.ServiceId == serviceId);
    }

    public async Task<IReadOnlyList<Worker>> GetAllAsync(int limit = 100, int offset = 0)
    {
        return await _context.Workers
            .AsNoTracking()
            .OrderByDescending(w => w.LastHeartbeat)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<Worker>> GetByStatusAsync(string status, int limit = 100, int offset = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        return await _context.Workers
            .AsNoTracking()
            .Where(w => w.Status == status)
            .OrderByDescending(w => w.LastHeartbeat)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync(int limit = 100)
    {
        return await _context.Workers
            .AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && w.FreeSlots > 0 && !w.IsDisabled)
            .OrderByDescending(w => w.FreeSlots) // Prefer workers with more capacity
            .ThenBy(w => w.ActiveJobs) // Then prefer less loaded workers
            .Take(limit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<Worker>> GetWorkersByCapabilitiesAsync(string[] requiredCapabilities, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        if (requiredCapabilities.Length == 0)
        {
            return await GetAvailableWorkersAsync(limit);
        }

        // Get available workers and filter by capabilities in memory
        // Note: This is not optimal for large datasets, but works for typical worker counts
        List<Worker> availableWorkers = await _context.Workers
            .AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && w.FreeSlots > 0 && !w.IsDisabled)
            .ToListAsync();

        List<Worker> matchingWorkers = availableWorkers
            .Where(w =>
            {
                try
                {
                    string[]? workerCapabilities = JsonSerializer.Deserialize<string[]>(w.CapabilitiesJson);
                    if (workerCapabilities == null)
                    {
                        return false;
                    }

                    // Check if worker has all required capabilities
                    return requiredCapabilities.All(required =>
                        workerCapabilities.Contains(required, StringComparer.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            })
            .OrderByDescending(w => w.FreeSlots)
            .ThenBy(w => w.ActiveJobs)
            .Take(limit)
            .ToList();

        return matchingWorkers;
    }

    public async Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout)
    {
        DateTime cutoffTime = DateTime.UtcNow - heartbeatTimeout;

        return await _context.Workers
            .AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && w.LastHeartbeat < cutoffTime)
            .ToListAsync();
    }

    public async Task UpdateStatusAsync(Guid id, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.Status = status;
            worker.UpdatedAt = DateTime.UtcNow;

            if (status == WorkerStatus.Online)
            {
                worker.OnlineAt = DateTime.UtcNow;
            }
            else if (status == WorkerStatus.Offline)
            {
                worker.OfflineAt = DateTime.UtcNow;
            }
        }
    }

    public async Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.LastHeartbeat = DateTime.UtcNow;
            // FreeSlots is now calculated as TotalSlots - ActiveJobs
            // Calculate ActiveJobs from the reported freeSlots
            worker.TotalSlots = totalSlots;
            worker.ActiveJobs = totalSlots - freeSlots;
            worker.UpdatedAt = DateTime.UtcNow;

            // Update status based on availability
            if (freeSlots > 0 && worker.Status != WorkerStatus.Draining)
            {
                worker.Status = WorkerStatus.Online;
            }
            else if (freeSlots == 0)
            {
                worker.Status = WorkerStatus.Busy;
            }
        }
    }

    public async Task IncrementActiveJobsAsync(Guid id)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.ActiveJobs++;
            // FreeSlots is calculated as TotalSlots - ActiveJobs
            worker.UpdatedAt = DateTime.UtcNow;

            if (worker.FreeSlots == 0)
            {
                worker.Status = WorkerStatus.Busy;
            }
        }
    }

    public async Task DecrementActiveJobsAsync(Guid id, bool success, double processingTimeSeconds)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.ActiveJobs = Math.Max(0, worker.ActiveJobs - 1);
            // FreeSlots is calculated as TotalSlots - ActiveJobs
            worker.UpdatedAt = DateTime.UtcNow;

            if (success)
            {
                worker.CompletedJobs++;
            }
            else
            {
                worker.FailedJobs++;
            }

            // Update average processing time (exponential moving average)
            if (worker.AverageProcessingTimeSeconds == null)
            {
                worker.AverageProcessingTimeSeconds = processingTimeSeconds;
            }
            else
            {
                // Alpha = 0.2 for smoothing
                worker.AverageProcessingTimeSeconds =
                    (0.2 * processingTimeSeconds) + (0.8 * worker.AverageProcessingTimeSeconds.Value);
            }

            // Update status if worker became available
            if (worker.FreeSlots > 0 && worker.Status == WorkerStatus.Busy && !worker.IsDisabled)
            {
                worker.Status = WorkerStatus.Online;
            }
        }
    }

    public async Task DisableWorkerAsync(Guid id, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.IsDisabled = true;
            worker.DisabledReason = reason;
            worker.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task EnableWorkerAsync(Guid id)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.IsDisabled = false;
            worker.DisabledReason = null;
            worker.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            _ = _context.Workers.Remove(worker);
        }
    }

    public async Task UpdateTotalSlotsAsync(Guid id, int totalSlots)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.TotalSlots = totalSlots;
            // NOTE: Do NOT recalculate ActiveJobs or FreeSlots here.
            // ActiveJobs is managed exclusively by JobDispatcherService (increment on dispatch, decrement on complete).
            // FreeSlots is maintained by the worker heartbeat.
            // Simply update the total slots capacity.
            worker.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task SaveChangesAsync()
    {
        _ = await _context.SaveChangesAsync();
    }
}
