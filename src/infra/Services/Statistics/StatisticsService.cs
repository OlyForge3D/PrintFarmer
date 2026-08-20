using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for computing aggregated print statistics.
/// </summary>
public class StatisticsService(AppDbContext db, IPrintFarmerTelemetryService? telemetry = null) : IStatisticsService
{
    /// <summary>Default page size for <see cref="GetCostsByJobAsync"/> when the caller omits one.</summary>
    public const int DefaultCostsByJobPageSize = 200;

    /// <summary>
    /// Server-side maximum page size for <see cref="GetCostsByJobAsync"/>. Requested page
    /// sizes are clamped to this value regardless of caller input, so the response payload
    /// is always bounded (issue #1734).
    /// </summary>
    public const int MaxCostsByJobPageSize = 500;

    private readonly AppDbContext _db = db;
    private readonly IPrintFarmerTelemetryService? _telemetry = telemetry;

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

        List<StatisticsSummaryAggregate> aggregateParts = await BuildSummaryAggregateQuery(query)
            .ToListAsync(ct);

        // Value-converted TimeSpan ticks are not provider-translatable as a SUM, so stream
        // scalar durations without materializing the full result set or overflowing a long total.
        double totalPrintHours = 0;
        await foreach (long ticks in query
            .Where(j => j.ActualPrintTime.HasValue)
            .Select(j => j.ActualPrintTime!.Value.Ticks)
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            totalPrintHours += TimeSpan.FromTicks(ticks).TotalHours;
        }

        int totalJobs = aggregateParts.Sum(part => part.TotalJobs);
        int completed = aggregateParts.Sum(part => part.Completed);
        int failed = aggregateParts.Sum(part => part.Failed);
        int cancelled = aggregateParts.Sum(part => part.Cancelled);
        int finishedJobs = completed + failed + cancelled;
        double successRate = finishedJobs > 0 ? (double)completed / finishedJobs * 100 : 0;

        return new StatisticsSummaryDto
        {
            TotalJobs = totalJobs,
            CompletedJobs = completed,
            FailedJobs = failed,
            CancelledJobs = cancelled,
            SuccessRate = Math.Round(successRate, 1),
            TotalCost = aggregateParts.Sum(part => part.TotalCost),
            TotalFilamentGrams = Math.Round(aggregateParts.Sum(part => part.TotalFilamentGrams), 1),
            TotalPrintHours = Math.Round(totalPrintHours, 1),
        };
    }

    internal static IQueryable<StatisticsSummaryAggregate> BuildSummaryAggregateQuery(IQueryable<PrintJob> query)
    {
        // The key must reference a column because SQL Server rejects constant-only GROUP BY
        // expressions. PrintJob IDs are generated non-empty keys, so this remains one bucket.
        return query
            .GroupBy(j => j.Id != Guid.Empty)
            .Select(g => new StatisticsSummaryAggregate(
                g.Count(),
                g.Count(j => j.Status == PrintJobStatus.Completed),
                g.Count(j => j.Status == PrintJobStatus.Failed),
                g.Count(j => j.Status == PrintJobStatus.Cancelled),
                g.Sum(j => j.ActualCost ?? 0m),
                g.Sum(j => j.ActualFilamentUsage ?? 0d)));
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

        // Aggregate server-side (mirrors BuildSummaryAggregateQuery below) instead of
        // materializing every matching PrintJob just to sum a handful of scalars.
        // The GroupBy produces zero or one row: Enumerable.Sum() over an empty list
        // safely yields 0 for the empty date-range case, no null-handling required.
        List<CostsSummaryAggregate> aggregateParts = await BuildCostsSummaryAggregateQuery(query)
            .ToListAsync(ct);

        decimal totalCost = aggregateParts.Sum(part => part.TotalCost);
        decimal totalMaterial = aggregateParts.Sum(part => part.MaterialCost);
        decimal totalEnergy = aggregateParts.Sum(part => part.EnergyCost);
        decimal totalMachine = aggregateParts.Sum(part => part.MachineCost);
        decimal totalLabor = aggregateParts.Sum(part => part.LaborCost);
        int jobCount = aggregateParts.Sum(part => part.JobCount);

        var materialGroup = await query
            .Where(j => !string.IsNullOrEmpty(j.FilamentName))
            .GroupBy(j => j.FilamentName!)
            .Select(g => new { Material = g.Key, Cost = g.Sum(j => j.TotalCostUsd ?? 0m) })
            .OrderByDescending(g => g.Cost)
            .FirstOrDefaultAsync(ct);

        return new CostStatisticsSummaryDto
        {
            TotalCostUsd = totalCost,
            AverageCostPerJobUsd = jobCount > 0 ? totalCost / jobCount : 0m,
            JobsWithCostData = jobCount,
            TotalMaterialCostUsd = totalMaterial,
            TotalEnergyCostUsd = totalEnergy,
            TotalMachineTimeCostUsd = totalMachine,
            TotalLaborCostUsd = totalLabor,
            MostExpensiveMaterial = materialGroup?.Material,
            MostExpensiveMaterialCost = materialGroup?.Cost ?? 0m,
        };
    }

    // Builds the single server-side aggregate projection backing GetCostsSummaryAsync.
    // Internal (not private) so provider-translation tests can call ToQueryString() on it
    // directly, mirroring BuildSummaryAggregateQuery above.
    internal static IQueryable<CostsSummaryAggregate> BuildCostsSummaryAggregateQuery(IQueryable<PrintJob> query)
    {
        // The key must reference a real column because SQL Server rejects constant-only
        // GROUP BY expressions (see BuildSummaryAggregateQuery for the same constraint).
        return query
            .GroupBy(j => j.Id != Guid.Empty)
            .Select(g => new CostsSummaryAggregate(
                g.Sum(j => j.TotalCostUsd ?? 0m),
                g.Sum(j => j.MaterialCostUsd ?? 0m),
                g.Sum(j => j.EnergyCostUsd ?? 0m),
                g.Sum(j => j.MachineTimeCostUsd ?? 0m),
                g.Sum(j => j.LaborCostUsd ?? 0m),
                g.Count()));
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
    public async Task<CostByJobPageDto> GetCostsByJobAsync(
        int? days,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var (effectiveStart, effectiveEnd) = ResolveEffectiveDateRange(days, startDate, endDate);

        int requestedPageSize = pageSize ?? DefaultCostsByJobPageSize;
        bool cappedToMaxPageSize = requestedPageSize > MaxCostsByJobPageSize;
        int effectivePageSize = Math.Clamp(requestedPageSize, 1, MaxCostsByJobPageSize);

        CostByJobCursor? decodedCursor = null;
        if (cursor is not null && !CostByJobCursor.TryDecode(cursor, out decodedCursor))
        {
            throw new ArgumentException("Invalid pagination cursor.", nameof(cursor));
        }

        // Dropped the AssignedPrinter/GcodeFile Include()s here: none of their navigation
        // properties beyond the two scalar names below are used, and both names are
        // already projected without eager-loading the full related entities (issue #1734).
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

        if (decodedCursor is not null)
        {
            DateTime cursorCompletedAt = new(decodedCursor.CompletedAtTicks, DateTimeKind.Utc);
            Guid cursorJobId = decodedCursor.JobId;
            query = query.Where(j =>
                (j.ActualEndTime ?? DateTime.MinValue) < cursorCompletedAt ||
                ((j.ActualEndTime ?? DateTime.MinValue) == cursorCompletedAt && j.Id.CompareTo(cursorJobId) < 0));
        }

        // Fetch one extra row to detect whether a next page exists without a second round trip.
        var rows = await query
            .OrderByDescending(j => j.ActualEndTime ?? DateTime.MinValue)
            .ThenByDescending(j => j.Id)
            .Select(j => new
            {
                j.Id,
                j.Name,
                GcodeFileName = j.GcodeFile != null ? j.GcodeFile.Name : null,
                PrinterName = j.AssignedPrinter != null ? j.AssignedPrinter.Name : null,
                j.FilamentName,
                j.RequiredMaterialType,
                j.ActualFilamentUsage,
                j.TotalCostUsd,
                j.MaterialCostUsd,
                j.EnergyCostUsd,
                j.MachineTimeCostUsd,
                j.LaborCostUsd,
                j.ActualPrintTime,
                j.ActualEndTime,
            })
            .Take(effectivePageSize + 1)
            .ToListAsync(ct);

        bool hasNextPage = rows.Count > effectivePageSize;
        if (hasNextPage)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        List<CostByJobDto> items = rows.Select(j => new CostByJobDto
        {
            JobId = j.Id,
            JobName = j.Name ?? j.GcodeFileName ?? "Untitled",
            PrinterName = j.PrinterName,
            FilamentName = j.FilamentName,
            MaterialType = j.RequiredMaterialType,
            FilamentUsedGrams = j.ActualFilamentUsage,
            TotalCostUsd = j.TotalCostUsd ?? 0m,
            MaterialCostUsd = j.MaterialCostUsd ?? 0m,
            EnergyCostUsd = j.EnergyCostUsd ?? 0m,
            MachineTimeCostUsd = j.MachineTimeCostUsd ?? 0m,
            LaborCostUsd = j.LaborCostUsd ?? 0m,
            PrintTimeSeconds = j.ActualPrintTime?.TotalSeconds,
            CompletedAt = j.ActualEndTime,
        }).ToList();

        string? nextCursor = null;
        if (hasNextPage)
        {
            var last = rows[^1];
            nextCursor = CostByJobCursor.FromRow(last.ActualEndTime, last.Id).Encode();
        }

        if (_telemetry is not null)
        {
            long payloadBytes = JsonSerializer.SerializeToUtf8Bytes(items).LongLength;
            _telemetry.RecordPagedQuery("costs/by-job", items.Count, payloadBytes, cappedToMaxPageSize);
        }

        return new CostByJobPageDto { Items = items, NextCursor = nextCursor };
    }

    internal sealed record StatisticsSummaryAggregate(
        int TotalJobs,
        int Completed,
        int Failed,
        int Cancelled,
        decimal TotalCost,
        double TotalFilamentGrams);

    internal sealed record CostsSummaryAggregate(
        decimal TotalCost,
        decimal MaterialCost,
        decimal EnergyCost,
        decimal MachineCost,
        decimal LaborCost,
        int JobCount);
}
