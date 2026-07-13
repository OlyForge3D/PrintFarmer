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
            .Include(l => l.MaintenanceTask)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public async Task<List<MaintenanceLog>> GetByPrinterIdAsync(Guid printerId, CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceLogs
            .Include(l => l.MaintenanceTask)
            .Where(l => l.PrinterId == printerId)
            .OrderByDescending(l => l.PerformedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<MaintenanceLog>> GetByPrinterAndTaskAsync(Guid printerId, Guid taskId, CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceLogs
            .Where(l => l.PrinterId == printerId && l.MaintenanceTaskId == taskId)
            .OrderByDescending(l => l.PerformedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceLog?> GetLastMaintenanceAsync(Guid printerId, Guid taskId, CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceLogs
            .Where(l => l.PrinterId == printerId && l.MaintenanceTaskId == taskId)
            .OrderByDescending(l => l.PerformedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<MaintenanceLog>> GetAllAsync(DateTime? startDate = null, DateTime? endDate = null, CancellationToken cancellationToken = default)
    {
        IQueryable<MaintenanceLog> query = _context.MaintenanceLogs
            .Include(l => l.Printer)
            .Include(l => l.MaintenanceTask);

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

    public async Task<List<MaintenanceLog>> GetByPrinterIdsAsync(IEnumerable<Guid> printerIds, CancellationToken cancellationToken = default)
    {
        List<Guid> idList = printerIds.ToList();
        return await _context.MaintenanceLogs
            .AsNoTracking()
            .Where(l => idList.Contains(l.PrinterId))
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

    #region Analytics

    public async Task<List<MaintenanceTrendEntry>> GetTrendsAsync(DateTime startDate, DateTime endDate, bool includeToolheadScope = true, CancellationToken cancellationToken = default)
    {
        IQueryable<MaintenanceLog> query = _context.MaintenanceLogs
            .Include(l => l.Printer)
            .Where(l => l.PerformedAt >= startDate && l.PerformedAt <= endDate);

        // Finding H5 (issue #711): when the multi-slot fallback feature is off,
        // per-toolhead maintenance must not surface in analytics; only printer-wide
        // logs (ToolheadId == null) are reported.
        if (!includeToolheadScope)
        {
            query = query.Where(l => l.ToolheadId == null);
        }

        var logs = await query
            .OrderByDescending(l => l.PerformedAt)
            .ToListAsync(cancellationToken);

        return logs.Select(l => new MaintenanceTrendEntry(
            l.PerformedAt.Date,
            l.Printer?.Name ?? "Unknown",
            l.Component,
            l.TaskName ?? "Maintenance",
            l.Cost ?? 0m)).ToList();
    }

    public async Task<List<ComponentLifespanEntry>> GetComponentLifespanAsync(bool includeToolheadScope = true, CancellationToken cancellationToken = default)
    {
        // Get all logs grouped by component
        IQueryable<MaintenanceLog> source = _context.MaintenanceLogs
            .Where(l => l.Component != null && l.Component != string.Empty);

        // Finding H5 (issue #711): exclude per-toolhead logs when the feature is off.
        if (!includeToolheadScope)
        {
            source = source.Where(l => l.ToolheadId == null);
        }

        var componentLogs = await source
            .GroupBy(l => l.Component!)
            .Select(g => new
            {
                Component = g.Key,
                Replacements = g.Count(),
                Logs = g.OrderBy(l => l.PerformedAt).ToList()
            })
            .ToListAsync(cancellationToken);

        var result = new List<ComponentLifespanEntry>();

        foreach (var componentGroup in componentLogs)
        {
            double avgLifespanHours = 0;

            // Calculate average lifespan between replacements using printer hours
            var logsWithHours = componentGroup.Logs
                .Where(l => l.PrinterHoursAtMaintenance.HasValue)
                .OrderBy(l => l.PerformedAt)
                .ToList();

            if (logsWithHours.Count >= 2)
            {
                var lifespans = new List<double>();
                for (int i = 1; i < logsWithHours.Count; i++)
                {
                    double diff = logsWithHours[i].PrinterHoursAtMaintenance!.Value - logsWithHours[i - 1].PrinterHoursAtMaintenance!.Value;
                    if (diff > 0)
                    {
                        lifespans.Add(diff);
                    }
                }

                if (lifespans.Count > 0)
                {
                    avgLifespanHours = lifespans.Average();
                }
            }

            // If no printer hours data, estimate from time between maintenances (assuming 8 hours printing per day)
            if (avgLifespanHours <= 0.001 && componentGroup.Logs.Count >= 2)
            {
                var sortedLogs = componentGroup.Logs.OrderBy(l => l.PerformedAt).ToList();
                var daysBetween = new List<double>();
                for (int i = 1; i < sortedLogs.Count; i++)
                {
                    double days = (sortedLogs[i].PerformedAt - sortedLogs[i - 1].PerformedAt).TotalDays;
                    if (days > 0)
                    {
                        daysBetween.Add(days);
                    }
                }

                if (daysBetween.Count > 0)
                {
                    // Estimate 8 print hours per day average
                    avgLifespanHours = daysBetween.Average() * 8;
                }
            }

            result.Add(new ComponentLifespanEntry(
                componentGroup.Component,
                Math.Round(avgLifespanHours, 1),
                componentGroup.Replacements));
        }

        return result.OrderByDescending(c => c.Replacements).ToList();
    }

    public async Task<List<MaintenanceCostEntry>> GetCostAnalysisAsync(int months = 12, bool includeToolheadScope = true, CancellationToken cancellationToken = default)
    {
        var startDate = DateTime.UtcNow.AddMonths(-months);

        IQueryable<MaintenanceLog> query = _context.MaintenanceLogs
            .Where(l => l.PerformedAt >= startDate && l.Cost.HasValue && l.Cost > 0);

        // Finding H5 (issue #711): exclude per-toolhead logs when the feature is off.
        if (!includeToolheadScope)
        {
            query = query.Where(l => l.ToolheadId == null);
        }

        var logs = await query.ToListAsync(cancellationToken);

        // Group by year-month and sum costs
        var grouped = logs
            .GroupBy(l => new { l.PerformedAt.Year, l.PerformedAt.Month })
            .Select(g => new MaintenanceCostEntry(
                $"{g.Key.Year}-{g.Key.Month:D2}",
                g.Sum(l => l.Cost ?? 0m)))
            .OrderBy(e => e.Month)
            .ToList();

        return grouped;
    }

    public async Task<List<PrinterUptimeEntry>> GetPrinterUptimeAsync(bool includeToolheadScope = true, CancellationToken cancellationToken = default)
    {
        // Get all printers with their maintenance logs and statistics
        var printers = await _context.Printers
            .Include(p => p.MaintenanceLogs)
            .Include(p => p.Statistics)
            .ToListAsync(cancellationToken);

        var result = new List<PrinterUptimeEntry>();

        foreach (var printer in printers)
        {
            // Finding H5 (issue #711): exclude per-toolhead logs from maintenance
            // counts and downtime when the multi-slot fallback feature is off.
            IEnumerable<MaintenanceLog> logs = includeToolheadScope
                ? printer.MaintenanceLogs
                : printer.MaintenanceLogs.Where(l => l.ToolheadId == null);
            var scopedLogs = logs.ToList();

            int maintenanceCount = scopedLogs.Count;
            int totalDowntimeMinutes = scopedLogs
                .Where(l => l.DurationMinutes.HasValue)
                .Sum(l => l.DurationMinutes!.Value);

            // Calculate uptime percentage based on total print hours vs maintenance downtime
            double uptimePercent = 100.0;
            double totalPrintHours = printer.Statistics?.TotalPrintHours ?? 0;

            if (totalPrintHours > 0 && totalDowntimeMinutes > 0)
            {
                double totalPrintMinutes = totalPrintHours * 60;
                double downtimeRatio = totalDowntimeMinutes / (totalPrintMinutes + totalDowntimeMinutes);
                uptimePercent = Math.Round((1 - downtimeRatio) * 100, 1);
            }

            result.Add(new PrinterUptimeEntry(
                printer.Name,
                printer.Id,
                uptimePercent,
                maintenanceCount,
                totalDowntimeMinutes));
        }

        return result.OrderByDescending(p => p.UptimePercent).ToList();
    }

    #endregion
}
