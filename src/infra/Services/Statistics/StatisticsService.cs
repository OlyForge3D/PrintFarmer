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

    /// <summary>
    /// Resolves the effective date range from query parameters.
    /// Priority: startDate/endDate > days > defaultDays > all-time.
    /// </summary>
    private static (DateTime? Start, DateTime? End) ResolveEffectiveDateRange(
        int? days, DateTime? startDate, DateTime? endDate, int? defaultDays = null)
    {
        if (startDate.HasValue || endDate.HasValue)
        {
            return (startDate, endDate);
        }

        if (days.HasValue)
        {
            int clamped = Math.Clamp(days.Value, 1, 730);
            return (DateTime.UtcNow.AddDays(-clamped), null);
        }

        if (defaultDays.HasValue)
        {
            return (DateTime.UtcNow.AddDays(-defaultDays.Value), null);
        }

        return (null, null);
    }

    public async Task<StatisticsSummaryDto> GetSummaryAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        var query = _db.Set<PrintJob>().AsQueryable();
        if (effectiveStart.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= effectiveStart.Value);
        }

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.QueuedAt <= effectiveEnd.Value);
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

    public async Task<List<DailyJobCountDto>> GetJobsOverTimeAsync(int? days = null, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate, defaultDays: 30);
        var since = effectiveStart ?? DateTime.UtcNow.AddDays(-30);
        var until = effectiveEnd ?? DateTime.UtcNow;

        var query = _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed
                     || j.Status == PrintJobStatus.Failed
                     || j.Status == PrintJobStatus.Cancelled);

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.QueuedAt <= effectiveEnd.Value);
        }

        var rows = await query
            .GroupBy(j => new { j.QueuedAt.Date, j.Status })
            .Select(g => new { g.Key.Date, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var result = new List<DailyJobCountDto>();
        for (var d = since.Date; d <= until.Date; d = d.AddDays(1))
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

    public async Task<List<DailyCostDto>> GetCostOverTimeAsync(int? days = null, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate, defaultDays: 30);
        var since = effectiveStart ?? DateTime.UtcNow.AddDays(-30);
        var until = effectiveEnd ?? DateTime.UtcNow;

        var query = _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since && j.ActualCost.HasValue);

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.QueuedAt <= effectiveEnd.Value);
        }

        var rows = await query
            .GroupBy(j => j.QueuedAt.Date)
            .Select(g => new { Date = g.Key, TotalCost = g.Sum(j => j.ActualCost!.Value) })
            .ToListAsync(ct);

        var result = new List<DailyCostDto>();
        for (var d = since.Date; d <= until.Date; d = d.AddDays(1))
        {
            result.Add(new DailyCostDto
            {
                Date = d.ToString("yyyy-MM-dd"),
                Cost = rows.FirstOrDefault(r => r.Date == d)?.TotalCost ?? 0m,
            });
        }

        return result;
    }

    public async Task<List<FilamentByMaterialDto>> GetFilamentByMaterialAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        var query = _db.Set<PrintJob>()
            .Where(j => j.ActualFilamentUsage.HasValue && j.ActualFilamentUsage > 0);

        if (effectiveStart.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= effectiveStart.Value);
        }

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.QueuedAt <= effectiveEnd.Value);
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

    public async Task<List<PrinterUtilizationDto>> GetPrinterUtilizationAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        var query = _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId.HasValue);

        if (effectiveStart.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= effectiveStart.Value);
        }

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.QueuedAt <= effectiveEnd.Value);
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

    /// <inheritdoc />
    public async Task<CostStatisticsSummaryDto> GetCostsSummaryAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        var query = _db.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Completed && j.TotalCostUsd.HasValue);

        if (effectiveStart.HasValue)
        {
            query = query.Where(j => j.ActualEndTime >= effectiveStart.Value);
        }

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.ActualEndTime <= effectiveEnd.Value);
        }

        var jobs = await query.ToListAsync(ct);

        decimal totalCost = jobs.Sum(j => j.TotalCostUsd ?? 0m);
        int jobCount = jobs.Count;
        decimal totalMaterial = jobs.Sum(j => j.MaterialCostUsd ?? 0m);
        decimal totalEnergy = jobs.Sum(j => j.EnergyCostUsd ?? 0m);
        decimal totalMachine = jobs.Sum(j => j.MachineTimeCostUsd ?? 0m);
        decimal totalLabor = jobs.Sum(j => j.LaborCostUsd ?? 0m);

        var materialGroups = jobs
            .Where(j => !string.IsNullOrEmpty(j.FilamentName) && j.TotalCostUsd.HasValue)
            .GroupBy(j => j.FilamentName!)
            .Select(g => new { Material = g.Key, Cost = g.Sum(j => j.TotalCostUsd!.Value) })
            .OrderByDescending(g => g.Cost)
            .FirstOrDefault();

        return new CostStatisticsSummaryDto
        {
            TotalCostUsd = totalCost,
            AverageCostPerJobUsd = jobCount > 0 ? totalCost / jobCount : 0m,
            JobsWithCostData = jobCount,
            TotalMaterialCostUsd = totalMaterial,
            TotalEnergyCostUsd = totalEnergy,
            TotalMachineTimeCostUsd = totalMachine,
            TotalLaborCostUsd = totalLabor,
            MostExpensiveMaterial = materialGroups?.Material,
            MostExpensiveMaterialCost = materialGroups?.Cost ?? 0m,
        };
    }

    /// <inheritdoc />
    public async Task<List<CostByTimePeriodDto>> GetCostsByTimePeriodAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate, defaultDays: 30);
        var since = effectiveStart ?? DateTime.UtcNow.AddDays(-30);

        var query = _db.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Completed && j.ActualEndTime >= since && j.TotalCostUsd.HasValue);

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.ActualEndTime <= effectiveEnd.Value);
        }

        var rows = await query
            .GroupBy(j => j.ActualEndTime!.Value.Date)
            .Select(g => new
            {
                Date = g.Key,
                TotalCost = g.Sum(j => j.TotalCostUsd ?? 0m),
                MaterialCost = g.Sum(j => j.MaterialCostUsd ?? 0m),
                EnergyCost = g.Sum(j => j.EnergyCostUsd ?? 0m),
                MachineCost = g.Sum(j => j.MachineTimeCostUsd ?? 0m),
                LaborCost = g.Sum(j => j.LaborCostUsd ?? 0m),
                JobCount = g.Count(),
            })
            .ToListAsync(ct);

        return rows.Select(r => new CostByTimePeriodDto
        {
            Date = r.Date,
            TotalCostUsd = r.TotalCost,
            MaterialCostUsd = r.MaterialCost,
            EnergyCostUsd = r.EnergyCost,
            MachineTimeCostUsd = r.MachineCost,
            LaborCostUsd = r.LaborCost,
            JobCount = r.JobCount,
        })
        .OrderBy(r => r.Date)
        .ToList();
    }

    /// <inheritdoc />
    public async Task<List<CostByPrinterDto>> GetCostsByPrinterAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        var query = _db.PrintJobs
            .Include(j => j.AssignedPrinter)
            .Where(j => j.Status == PrintJobStatus.Completed && j.TotalCostUsd.HasValue && j.AssignedPrinterId != null);

        if (effectiveStart.HasValue)
        {
            query = query.Where(j => j.ActualEndTime >= effectiveStart.Value);
        }

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.ActualEndTime <= effectiveEnd.Value);
        }

        var rows = await query
            .GroupBy(j => new { j.AssignedPrinterId, PrinterName = j.AssignedPrinter!.Name })
            .Select(g => new
            {
                PrinterId = g.Key.AssignedPrinterId!.Value,
                PrinterName = g.Key.PrinterName,
                TotalCost = g.Sum(j => j.TotalCostUsd ?? 0m),
                JobCount = g.Count(),
                MaterialCost = g.Sum(j => j.MaterialCostUsd ?? 0m),
                EnergyCost = g.Sum(j => j.EnergyCostUsd ?? 0m),
                MachineCost = g.Sum(j => j.MachineTimeCostUsd ?? 0m),
                LaborCost = g.Sum(j => j.LaborCostUsd ?? 0m),
            })
            .ToListAsync(ct);

        return rows.Select(r => new CostByPrinterDto
        {
            PrinterId = r.PrinterId,
            PrinterName = r.PrinterName,
            TotalCostUsd = r.TotalCost,
            AverageCostPerJobUsd = r.JobCount > 0 ? r.TotalCost / r.JobCount : 0m,
            JobCount = r.JobCount,
            MaterialCostUsd = r.MaterialCost,
            EnergyCostUsd = r.EnergyCost,
            MachineTimeCostUsd = r.MachineCost,
            LaborCostUsd = r.LaborCost,
        })
        .OrderByDescending(r => r.TotalCostUsd)
        .ToList();
    }

    /// <inheritdoc />
    public async Task<List<CostByMaterialDto>> GetCostsByMaterialAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        var query = _db.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Completed && j.TotalCostUsd.HasValue && !string.IsNullOrEmpty(j.FilamentName));

        if (effectiveStart.HasValue)
        {
            query = query.Where(j => j.ActualEndTime >= effectiveStart.Value);
        }

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.ActualEndTime <= effectiveEnd.Value);
        }

        var rows = await query
            .GroupBy(j => j.FilamentName!)
            .Select(g => new
            {
                MaterialType = g.Key,
                TotalCost = g.Sum(j => j.TotalCostUsd ?? 0m),
                JobCount = g.Count(),
                TotalFilamentUsage = g.Sum(j => j.ActualFilamentUsage ?? 0.0),
            })
            .ToListAsync(ct);

        return rows.Select(r => new CostByMaterialDto
        {
            MaterialType = r.MaterialType,
            TotalCostUsd = r.TotalCost,
            AverageCostPerJobUsd = r.JobCount > 0 ? r.TotalCost / r.JobCount : 0m,
            JobCount = r.JobCount,
            TotalFilamentUsageGrams = r.TotalFilamentUsage,
        })
        .OrderByDescending(r => r.TotalCostUsd)
        .ToList();
    }

    /// <inheritdoc/>
    public async Task<List<CostByJobDto>> GetCostsByJobAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        var query = _db.PrintJobs
            .Include(j => j.AssignedPrinter)
            .Include(j => j.GcodeFile)
            .Where(j => j.Status == PrintJobStatus.Completed && j.TotalCostUsd.HasValue);

        if (effectiveStart.HasValue)
        {
            query = query.Where(j => j.ActualEndTime >= effectiveStart.Value);
        }

        if (effectiveEnd.HasValue)
        {
            query = query.Where(j => j.ActualEndTime <= effectiveEnd.Value);
        }

        var rows = await query
            .OrderByDescending(j => j.ActualEndTime)
            .Select(j => new CostByJobDto
            {
                JobId = j.Id,
                JobName = j.Name ?? (j.GcodeFile != null ? j.GcodeFile.Name : "Untitled"),
                PrinterName = j.AssignedPrinter != null ? j.AssignedPrinter.Name : null,
                FilamentName = j.FilamentName,
                MaterialType = j.RequiredMaterialType,
                FilamentUsedGrams = j.ActualFilamentUsage,
                TotalCostUsd = j.TotalCostUsd ?? 0m,
                MaterialCostUsd = j.MaterialCostUsd ?? 0m,
                EnergyCostUsd = j.EnergyCostUsd ?? 0m,
                MachineTimeCostUsd = j.MachineTimeCostUsd ?? 0m,
                LaborCostUsd = j.LaborCostUsd ?? 0m,
                PrintTimeSeconds = j.ActualPrintTime.HasValue ? j.ActualPrintTime.Value.TotalSeconds : null,
                CompletedAt = j.ActualEndTime,
            })
            .ToListAsync(ct);

        return rows;
    }
}
