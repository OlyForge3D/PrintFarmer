using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Entity Framework implementation of the IMaintenanceLogRepository interface.
/// Manages persistence and querying of MaintenanceLog entities.
/// </summary>
public class EfMaintenanceLogRepository(AppDbContext context) : IMaintenanceLogRepository
{
    private readonly AppDbContext _context = context;

    public async Task<MaintenanceLog?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceLogs
            .Include(l => l.Printer)
            .Include(l => l.MaintenanceSchedule)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public async Task<List<MaintenanceLog>> GetByPrinterIdAsync(Guid printerId, CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceLogs
            .Include(l => l.MaintenanceSchedule)
            .Where(l => l.PrinterId == printerId)
            .OrderByDescending(l => l.PerformedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<MaintenanceLog>> GetByPrinterAndScheduleAsync(Guid printerId, Guid scheduleId, CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceLogs
            .Where(l => l.PrinterId == printerId && l.MaintenanceScheduleId == scheduleId)
            .OrderByDescending(l => l.PerformedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceLog?> GetLastMaintenanceAsync(Guid printerId, Guid scheduleId, CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceLogs
            .Where(l => l.PrinterId == printerId && l.MaintenanceScheduleId == scheduleId)
            .OrderByDescending(l => l.PerformedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<MaintenanceLog>> GetAllAsync(DateTime? startDate = null, DateTime? endDate = null, CancellationToken cancellationToken = default)
    {
        IQueryable<MaintenanceLog> query = _context.MaintenanceLogs
            .Include(l => l.Printer)
            .Include(l => l.MaintenanceSchedule);

        if (startDate.HasValue)
        {
            query = query.Where(l => l.PerformedAt >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(l => l.PerformedAt <= endDate.Value);
        }

        return await query
            .OrderByDescending(l => l.PerformedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceLog> AddAsync(MaintenanceLog log, CancellationToken cancellationToken = default)
    {
        _context.MaintenanceLogs.Add(log);
        await _context.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task<MaintenanceLog> UpdateAsync(MaintenanceLog log, CancellationToken cancellationToken = default)
    {
        _context.MaintenanceLogs.Update(log);
        await _context.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        MaintenanceLog? log = await _context.MaintenanceLogs.FindAsync([id], cancellationToken);
        if (log == null)
        {
            return false;
        }

        _context.MaintenanceLogs.Remove(log);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
