using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for computing aggregated print statistics.
/// </summary>
public class StatisticsService(AppDbContext db) : IStatisticsService
{
    private readonly AppDbContext _db = db;

    public async Task<StatisticsSummaryDto> GetSummaryAsync(int? days, CancellationToken ct = default)
    {
        int effectiveDays = days.HasValue ? Math.Clamp(days.Value, 1, 365) : 0;
        var since = effectiveDays > 0 ? DateTime.UtcNow.AddDays(-effectiveDays) : (DateTime?)null;

        var query = _db.Set<PrintJob>().AsQueryable();
        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        int totalJobs = await query.CountAsync(ct);
        int completed = await query.CountAsync(j => j.Status == PrintJobStatus.Completed, ct);
        int failed = await query.CountAsync(j => j.Status == PrintJobStatus.Failed, ct);
        int cancelled = await query.CountAsync(j => j.Status == PrintJobStatus.Cancelled, ct);

        decimal totalCost = await query
            .Where(j => j.ActualCost.HasValue)
            .SumAsync(j => j.ActualCost!.Value, ct);
        double totalFilamentGrams = await query
            .Where(j => j.ActualFilamentUsage.HasValue)
            .SumAsync(j => j.ActualFilamentUsage!.Value, ct);

        var ticksList = await query
            .Where(j => j.ActualPrintTime.HasValue)
            .Select(j => j.ActualPrintTime!.Value.Ticks)
            .ToListAsync(ct);
        double totalPrintHours = ticksList.Sum(t => TimeSpan.FromTicks(t).TotalHours);

        int finishedJobs = completed + failed + cancelled;
        double successRate = finishedJobs > 0 ? (double)completed / finishedJobs * 100 : 0;

        return new StatisticsSummaryDto
        {
            TotalJobs = totalJobs,
            CompletedJobs = completed,
            FailedJobs = failed,
            CancelledJobs = cancelled,
            SuccessRate = Math.Round(successRate, 1),
            TotalCost = totalCost,
            TotalFilamentGrams = Math.Round(totalFilamentGrams, 1),
            TotalPrintHours = Math.Round(totalPrintHours, 1),
        };
    }

    public async Task<List<DailyJobCountDto>> GetJobsOverTimeAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        var rows = await _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed
                     || j.Status == PrintJobStatus.Failed
                     || j.Status == PrintJobStatus.Cancelled)
            .GroupBy(j => new { j.QueuedAt.Date, j.Status })
            .Select(g => new { g.Key.Date, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var result = new List<DailyJobCountDto>();
        for (var d = since.Date; d <= DateTime.UtcNow.Date; d = d.AddDays(1))
        {
            result.Add(new DailyJobCountDto
            {
                Date = d.ToString("yyyy-MM-dd"),
                Completed = rows.Where(r => r.Date == d && r.Status == PrintJobStatus.Completed).Sum(r => r.Count),
                Failed = rows.Where(r => r.Date == d && r.Status == PrintJobStatus.Failed).Sum(r => r.Count),
                Cancelled = rows.Where(r => r.Date == d && r.Status == PrintJobStatus.Cancelled).Sum(r => r.Count),
            });
        }

        return result;
    }

    public async Task<List<DailyCostDto>> GetCostOverTimeAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        var rows = await _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since && j.ActualCost.HasValue)
            .GroupBy(j => j.QueuedAt.Date)
            .Select(g => new { Date = g.Key, TotalCost = g.Sum(j => j.ActualCost!.Value) })
            .ToListAsync(ct);

        var result = new List<DailyCostDto>();
        for (var d = since.Date; d <= DateTime.UtcNow.Date; d = d.AddDays(1))
        {
            result.Add(new DailyCostDto
            {
                Date = d.ToString("yyyy-MM-dd"),
                Cost = rows.FirstOrDefault(r => r.Date == d)?.TotalCost ?? 0m,
            });
        }

        return result;
    }

    public async Task<List<FilamentByMaterialDto>> GetFilamentByMaterialAsync(int? days, CancellationToken ct = default)
    {
        int? clampedDays = days.HasValue ? Math.Clamp(days.Value, 1, 365) : null;
        var query = _db.Set<PrintJob>()
            .Where(j => j.ActualFilamentUsage.HasValue && j.ActualFilamentUsage > 0);

        if (clampedDays.HasValue)
        {
            var since = DateTime.UtcNow.AddDays(-clampedDays.Value);
            query = query.Where(j => j.QueuedAt >= since);
        }

        var rows = await query
            .GroupBy(j => j.RequiredMaterialType ?? "Unknown")
            .Select(g => new FilamentByMaterialDto
            {
                Material = g.Key,
                Grams = g.Sum(j => j.ActualFilamentUsage!.Value),
            })
            .OrderByDescending(r => r.Grams)
            .ToListAsync(ct);

        var result = rows.Select(r => r with { Grams = Math.Round(r.Grams, 1) }).ToList();
        return result;
    }

    public async Task<List<PrinterUtilizationDto>> GetPrinterUtilizationAsync(int? days, CancellationToken ct = default)
    {
        int? clampedDays = days.HasValue ? Math.Clamp(days.Value, 1, 365) : null;
        var query = _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId.HasValue);

        if (clampedDays.HasValue)
        {
            var since = DateTime.UtcNow.AddDays(-clampedDays.Value);
            query = query.Where(j => j.QueuedAt >= since);
        }

        var rawJobs = await query
            .Select(j => new
            {
                PrinterId = j.AssignedPrinterId!.Value,
                j.Status,
                PrintTimeTicks = j.ActualPrintTime.HasValue ? j.ActualPrintTime.Value.Ticks : (long?)null,
            })
            .ToListAsync(ct);

        var rows = rawJobs
            .GroupBy(j => j.PrinterId)
            .Select(g => new
            {
                PrinterId = g.Key,
                TotalJobs = g.Count(),
                Completed = g.Count(j => j.Status == PrintJobStatus.Completed),
                Failed = g.Count(j => j.Status == PrintJobStatus.Failed),
                TotalHours = g.Where(j => j.PrintTimeTicks.HasValue)
                    .Sum(j => TimeSpan.FromTicks(j.PrintTimeTicks!.Value).TotalHours),
            })
            .ToList();

        var printerIds = rows.Select(r => r.PrinterId).ToList();
        var printerNames = await _db.Printers
            .Where(p => printerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var result = rows.Select(r => new PrinterUtilizationDto
        {
            PrinterId = r.PrinterId,
            PrinterName = printerNames.GetValueOrDefault(r.PrinterId, "Unknown"),
            TotalJobs = r.TotalJobs,
            CompletedJobs = r.Completed,
            FailedJobs = r.Failed,
            TotalPrintHours = Math.Round(r.TotalHours, 1),
            SuccessRate = r.Completed + r.Failed > 0
                ? Math.Round((double)r.Completed / (r.Completed + r.Failed) * 100, 1)
                : 0,
        })
        .OrderByDescending(r => r.TotalJobs)
        .ToList();

        return result;
    }
}
