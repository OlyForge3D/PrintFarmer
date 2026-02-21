using System.Text.Json;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IWorkerRepository"/> backed by <see cref="SlicerDbContext"/>.
/// </summary>
public class EfWorkerRepository(SlicerDbContext context) : IWorkerRepository
{
    private readonly SlicerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public async Task AddAsync(Worker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _ = await _context.Workers.AddAsync(worker);
    }

    /// <inheritdoc/>
    public async Task<Worker?> GetByIdAsync(Guid id)
    {
        return await _context.Workers.FirstOrDefaultAsync(w => w.Id == id);
    }

    /// <inheritdoc/>
    public async Task<Worker?> GetByServiceIdAsync(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        return await _context.Workers.FirstOrDefaultAsync(w => w.ServiceId == serviceId);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Worker>> GetAllAsync(int limit = 100, int offset = 0)
    {
        return await _context.Workers
            .AsNoTracking()
            .OrderByDescending(w => w.LastHeartbeat)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync(int limit = 100)
    {
        return await _context.Workers
            .AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && (w.TotalSlots - w.ActiveJobs) > 0 && !w.IsDisabled)
            .OrderByDescending(w => w.TotalSlots - w.ActiveJobs)
            .ThenBy(w => w.ActiveJobs)
            .Take(limit)
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Worker>> GetWorkersByCapabilitiesAsync(string[] requiredCapabilities, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        if (requiredCapabilities.Length == 0)
        {
            return await GetAvailableWorkersAsync(limit);
        }

        List<Worker> availableWorkers = await _context.Workers
            .AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && (w.TotalSlots - w.ActiveJobs) > 0 && !w.IsDisabled)
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout)
    {
        DateTime cutoffTime = DateTime.UtcNow - heartbeatTimeout;

        return await _context.Workers
            .AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && w.LastHeartbeat < cutoffTime)
            .ToListAsync();
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.LastHeartbeat = DateTime.UtcNow;
            worker.TotalSlots = totalSlots;
            worker.ActiveJobs = totalSlots - freeSlots;
            worker.UpdatedAt = DateTime.UtcNow;

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

    /// <inheritdoc/>
    public async Task IncrementActiveJobsAsync(Guid id)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.ActiveJobs++;
            worker.UpdatedAt = DateTime.UtcNow;

            if (worker.FreeSlots == 0)
            {
                worker.Status = WorkerStatus.Busy;
            }
        }
    }

    /// <inheritdoc/>
    public async Task DecrementActiveJobsAsync(Guid id, bool success, double processingTimeSeconds)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.ActiveJobs = Math.Max(0, worker.ActiveJobs - 1);
            worker.UpdatedAt = DateTime.UtcNow;

            if (success)
            {
                worker.CompletedJobs++;
            }
            else
            {
                worker.FailedJobs++;
            }

            if (worker.AverageProcessingTimeSeconds == null)
            {
                worker.AverageProcessingTimeSeconds = processingTimeSeconds;
            }
            else
            {
                worker.AverageProcessingTimeSeconds =
                    (0.2 * processingTimeSeconds) + (0.8 * worker.AverageProcessingTimeSeconds.Value);
            }

            if (worker.FreeSlots > 0 && worker.Status == WorkerStatus.Busy && !worker.IsDisabled)
            {
                worker.Status = WorkerStatus.Online;
            }
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            _ = _context.Workers.Remove(worker);
        }
    }

    /// <inheritdoc/>
    public async Task UpdateTotalSlotsAsync(Guid id, int totalSlots)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.TotalSlots = totalSlots;
            worker.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync()
    {
        _ = await _context.SaveChangesAsync();
    }
}
