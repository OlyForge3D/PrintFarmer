using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// EF Core implementation of printer maintenance schedule repository.
/// </summary>
public class EfPrinterMaintenanceScheduleRepository(AppDbContext context) : IPrinterMaintenanceScheduleRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<PrinterMaintenanceSchedule>> GetAllAsync(Guid? printerId = null, Guid? planId = null, bool? activeOnly = null, CancellationToken ct = default)
    {
        IQueryable<PrinterMaintenanceSchedule> query = _context.PrinterMaintenanceSchedules
            .AsNoTracking()
            .Include(s => s.MaintenancePlan)
            .Include(s => s.Printer);

        if (printerId.HasValue)
        {
            query = query.Where(s => s.PrinterId == printerId.Value);
        }

        if (planId.HasValue)
        {
            query = query.Where(s => s.MaintenancePlanId == planId.Value);
        }

        if (activeOnly == true)
        {
            query = query.Where(s => s.IsActive);
        }

        return await query
            .OrderByDescending(s => s.DeployedAt)
            .ToListAsync(ct);
    }

    public async Task<List<PrinterMaintenanceSchedule>> GetActiveWithTasksAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _context.PrinterMaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.PrinterId == printerId && s.IsActive)
            .Include(s => s.Printer)
            .Include(s => s.MaintenancePlan)
                .ThenInclude(p => p.PlanTasks)
                    .ThenInclude(pt => pt.MaintenanceTask)
            .OrderByDescending(s => s.DeployedAt)
            .ToListAsync(ct);
    }

    public async Task<PrinterMaintenanceSchedule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PrinterMaintenanceSchedules
            .Include(s => s.MaintenancePlan)
            .Include(s => s.Printer)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<bool> ExistsAsync(Guid planId, Guid printerId, CancellationToken ct = default)
    {
        return await _context.PrinterMaintenanceSchedules
            .AnyAsync(s => s.MaintenancePlanId == planId && s.PrinterId == printerId, ct);
    }

    public async Task AddAsync(PrinterMaintenanceSchedule schedule, CancellationToken ct = default)
    {
        await _context.PrinterMaintenanceSchedules.AddAsync(schedule, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PrinterMaintenanceSchedule schedule, CancellationToken ct = default)
    {
        _context.PrinterMaintenanceSchedules.Update(schedule);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(PrinterMaintenanceSchedule schedule, CancellationToken ct = default)
    {
        _context.PrinterMaintenanceSchedules.Remove(schedule);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
