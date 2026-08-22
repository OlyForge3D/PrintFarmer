using System.Text.Json;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

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

            worker.AverageProcessingTimeSeconds = worker.AverageProcessingTimeSeconds == null
                ? processingTimeSeconds
                : (0.2 * processingTimeSeconds) + (0.8 * worker.AverageProcessingTimeSeconds.Value);

            if (worker.FreeSlots > 0 && worker.Status == WorkerStatus.Busy && !worker.IsDisabled)
            {
                worker.Status = WorkerStatus.Online;
            }
        }
    }

    /// <inheritdoc/>
    public async Task DisableWorkerAsync(Guid id, string reason, WorkerDisableSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (source == WorkerDisableSource.None)
        {
            throw new ArgumentException(
                "A disable must be attributed to a source; None means 'not disabled'.",
                nameof(source));
        }

        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            worker.IsDisabled = true;
            worker.DisabledReason = reason;
            worker.DisableSource = source;
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
            worker.DisableSource = WorkerDisableSource.None;
            worker.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeForDeregistrationAsync(string serviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        DateTime now = DateTime.UtcNow;

        int affected = await _context.Workers
            .Where(w => w.ServiceId == serviceId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(w => w.Status, WorkerStatus.Offline)
                    .SetProperty(w => w.OfflineAt, (DateTime?)now)
                    .SetProperty(w => w.UpdatedAt, now)
                    .SetProperty(w => w.ApiKey, (string?)null)
                    .SetProperty(w => w.IsDisabled, true),
                ct);

        if (affected == 0)
        {
            return false;
        }

        _ = await _context.Workers
            .Where(w => w.ServiceId == serviceId && w.DisableSource != WorkerDisableSource.Administrator)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(w => w.DisabledReason, WorkerDisableReasons.Deregistered)
                    .SetProperty(w => w.DisableSource, WorkerDisableSource.Deregistration),
                ct);

        // These statements bypass the change tracker, so any instance already materialised in
        // this context still holds the pre-revocation values. Detach it rather than leave a stale
        // copy that a later SaveChangesAsync could write back over what was just committed.
        foreach (EntityEntry<Worker> entry in _context.ChangeTracker.Entries<Worker>()
            .Where(e => e.Entity.ServiceId == serviceId)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }

        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> ClearAutomaticDisableAsync(string serviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        int affected = await _context.Workers
            .Where(w => w.ServiceId == serviceId
                && w.IsDisabled
                && w.DisableSource != WorkerDisableSource.Administrator)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(w => w.IsDisabled, false)
                    .SetProperty(w => w.DisabledReason, (string?)null)
                    .SetProperty(w => w.DisableSource, WorkerDisableSource.None)
                    .SetProperty(w => w.UpdatedAt, DateTime.UtcNow),
                ct);

        // The statement above bypasses the change tracker, so a copy already materialised in this
        // context still holds whatever it read before. Refresh it, so the caller's own edits are
        // saved on top of the current row — including an administrator's ban committed in the
        // meantime, which this statement deliberately did not touch. Only untouched entries are
        // refreshed: reloading a modified one would silently discard the caller's work.
        foreach (EntityEntry<Worker> entry in _context.ChangeTracker.Entries<Worker>()
            .Where(e => e.State == EntityState.Unchanged && e.Entity.ServiceId == serviceId)
            .ToList())
        {
            await entry.ReloadAsync(ct);
        }

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> ResetAsync(Guid id)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker is null)
        {
            return false;
        }

        worker.ActiveJobs = 0;
        worker.UpdatedAt = DateTime.UtcNow;

        if (!worker.IsDisabled && worker.Status != WorkerStatus.Draining)
        {
            worker.Status = WorkerStatus.Online;
        }

        await _context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id)
    {
        Worker? worker = await _context.Workers.FindAsync(id);
        if (worker != null)
        {
            if (Guid.TryParse(worker.ServiceId, out Guid serviceId))
            {
                SlicerService? service = await _context.SlicerServices.FindAsync(serviceId);
                if (service != null)
                {
                    _ = _context.SlicerServices.Remove(service);
                }
            }

            _ = _context.Workers.Remove(worker);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteIfNotAdministrativelyDisabledAsync(Guid id, CancellationToken ct = default)
    {
        // Read the pairing before the delete: afterwards the row is gone and there is no way back
        // to the service it belonged to.
        string? serviceIdText = await _context.Workers
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => w.ServiceId)
            .FirstOrDefaultAsync(ct);

        if (serviceIdText is null)
        {
            return false;
        }

        // The worker row and its paired service row must go together. As two independent
        // statements, a failure or cancellation between them orphans the service permanently:
        // the stale sweep enumerates Workers, so a service with no worker is invisible to it and
        // no later pass can collect it. A concurrent registration could also reclaim the
        // surviving service and hang a fresh worker off it. Enlist in the caller's transaction
        // when there is one, rather than opening a second.
        IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            // The exemption is evaluated by the database inside the delete itself, so a ban
            // committed after the caller picked this worker still blocks the delete. Testing it in
            // memory first would only re-check the same stale snapshot the caller already holds.
            int affected = await _context.Workers
                .Where(w => w.Id == id
                    && !(w.IsDisabled && w.DisableSource == WorkerDisableSource.Administrator))
                .ExecuteDeleteAsync(ct);

            if (affected == 0)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(ct);
                }

                return false;
            }

            Guid? deletedServiceId = null;

            if (Guid.TryParse(serviceIdText, out Guid serviceId))
            {
                deletedServiceId = serviceId;

                _ = await _context.SlicerServices
                    .Where(s => s.Id == serviceId)
                    .ExecuteDeleteAsync(ct);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            // These statements bypass the change tracker, so any copy still materialised here
            // refers to rows that no longer exist. Drop them rather than let a later
            // SaveChangesAsync try to update or re-insert them.
            foreach (EntityEntry entry in _context.ChangeTracker.Entries<Worker>()
                .Where(e => e.Entity.Id == id)
                .Cast<EntityEntry>()
                .Concat(_context.ChangeTracker.Entries<SlicerService>()
                    .Where(e => deletedServiceId != null && e.Entity.Id == deletedServiceId)
                    .Cast<EntityEntry>())
                .ToList())
            {
                entry.State = EntityState.Detached;
            }

            return true;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
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
