using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// EF Core implementation of maintenance alert repository.
/// </summary>
public class EfMaintenanceAlertRepository(AppDbContext context) : IMaintenanceAlertRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<MaintenanceAlert>> GetActivePrinterAlertsAsync(
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceAlerts
            .AsNoTracking()
            .Where(a => a.PrinterId == printerId && a.Status == MaintenanceAlertStatus.Active)
            .Include(a => a.Printer)
            .Include(a => a.MaintenanceSchedule)
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<MaintenanceAlert>> GetAllPrinterAlertsAsync(
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceAlerts
            .AsNoTracking()
            .Where(a => a.PrinterId == printerId)
            .Include(a => a.Printer)
            .Include(a => a.MaintenanceSchedule)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<MaintenanceAlert>> GetAllActiveAlertsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceAlerts
            .AsNoTracking()
            .Where(a => a.Status == MaintenanceAlertStatus.Active)
            .Include(a => a.Printer)
            .Include(a => a.MaintenanceSchedule)
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceAlert?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceAlerts
            .AsNoTracking()
            .Include(a => a.Printer)
            .Include(a => a.MaintenanceSchedule)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<bool> HasActiveAlertAsync(
        Guid printerId,
        Guid scheduleId,
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceAlerts
            .AnyAsync(
                a => a.PrinterId == printerId
                    && a.MaintenanceScheduleId == scheduleId
                    && a.Status == MaintenanceAlertStatus.Active,
                cancellationToken);
    }

    public async Task AddAsync(MaintenanceAlert alert, CancellationToken cancellationToken = default)
    {
        alert.CreatedAt = DateTime.UtcNow;
        await _context.MaintenanceAlerts.AddAsync(alert, cancellationToken);
    }

    public Task UpdateAsync(MaintenanceAlert alert, CancellationToken cancellationToken = default)
    {
        _context.MaintenanceAlerts.Update(alert);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
