using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Computes performance correlations across materials, printers, and print settings.
/// </summary>
public class CorrelationAnalyticsService(AppDbContext db) : ICorrelationAnalyticsService
{
    private readonly AppDbContext _db = db;

    public async Task<List<MaterialSuccessRateDto>> GetMaterialSuccessRatesAsync(int? days, CancellationToken ct = default)
    {
        var since = ComputeSince(days);
        var query = _db.Set<PrintJob>()
            .Where(j => j.RequiredMaterialType != null)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var grouped = await query
            .GroupBy(j => j.RequiredMaterialType!)
            .Select(g => new
            {
                Material = g.Key,
                Total = g.Count(),
                Completed = g.Count(j => j.Status == PrintJobStatus.Completed),
            })
            .ToListAsync(ct);

        return grouped
            .Select(g => new MaterialSuccessRateDto
            {
                Material = g.Material,
                TotalJobs = g.Total,
                CompletedJobs = g.Completed,
                SuccessRate = g.Total > 0 ? Math.Round((double)g.Completed / g.Total * 100, 1) : 0,
            })
            .OrderByDescending(d => d.TotalJobs)
            .ToList();
    }

    public async Task<List<PrinterMaterialPerformanceDto>> GetPrinterMaterialPerformanceAsync(int? days, CancellationToken ct = default)
    {
        var since = ComputeSince(days);
        var query = _db.Set<PrintJob>()
            .Where(j => j.AssignedPrinterId.HasValue)
            .Where(j => j.RequiredMaterialType != null)
            .Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var rawData = await query
            .Select(j => new
            {
                PrinterId = j.AssignedPrinterId!.Value,
                Material = j.RequiredMaterialType!,
                j.Status,
            })
            .ToListAsync(ct);

        var printerIds = rawData.Select(r => r.PrinterId).Distinct().ToList();
        var printerNames = await _db.Printers
            .Where(p => printerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        return rawData
            .GroupBy(j => new { j.PrinterId, j.Material })
            .Select(g => new PrinterMaterialPerformanceDto
            {
                PrinterId = g.Key.PrinterId,
                PrinterName = printerNames.GetValueOrDefault(g.Key.PrinterId, "Unknown"),
                Material = g.Key.Material,
                TotalJobs = g.Count(),
                CompletedJobs = g.Count(j => j.Status == PrintJobStatus.Completed),
                SuccessRate = g.Any() ? Math.Round((double)g.Count(j => j.Status == PrintJobStatus.Completed) / g.Count() * 100, 1)
                    : 0,
            })
            .OrderByDescending(d => d.TotalJobs)
            .ToList();
    }

    public async Task<List<TemperatureQualityCorrelationDto>> GetTemperatureQualityDataAsync(int? days, CancellationToken ct = default)
    {
        var since = ComputeSince(days);

        var query = from job in _db.Set<PrintJob>()
                    join stats in _db.Set<PrintJobStatistics>() on job.Id equals stats.PrintJobId
                    where job.Status == PrintJobStatus.Completed || job.Status == PrintJobStatus.Failed
                    where stats.NozzleTemperature.HasValue && stats.BedTemperature.HasValue
                    select new { job, stats };

        if (since.HasValue)
        {
            query = query.Where(x => x.job.QueuedAt >= since.Value);
        }

        return await query
            .Select(x => new TemperatureQualityCorrelationDto
            {
                JobId = x.job.Id,
                NozzleTemp = x.stats.NozzleTemperature!.Value,
                BedTemp = x.stats.BedTemperature!.Value,
                Material = x.job.RequiredMaterialType ?? "Unknown",
                DurationMinutes = x.stats.ActualDurationMs.HasValue
                    ? x.stats.ActualDurationMs.Value / 60000.0
                    : 0,
                Success = x.job.Status == PrintJobStatus.Completed,
            })
            .ToListAsync(ct);
    }

    public async Task<List<DurationTrendDto>> GetDurationTrendsAsync(int? days, CancellationToken ct = default)
    {
        var since = ComputeSince(days);
        var query = _db.Set<PrintJob>()
            .Where(j => j.ActualPrintTime.HasValue);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        var jobs = await query
            .Select(j => new
            {
                j.QueuedAt,
                j.ActualPrintTime,
            })
            .ToListAsync(ct);

        return jobs
            .GroupBy(j => j.QueuedAt.Date)
            .Select(g => new DurationTrendDto
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                AverageDurationMinutes = Math.Round(g.Average(j => j.ActualPrintTime!.Value.TotalMinutes), 1),
                MinDurationMinutes = Math.Round(g.Min(j => j.ActualPrintTime!.Value.TotalMinutes), 1),
                MaxDurationMinutes = Math.Round(g.Max(j => j.ActualPrintTime!.Value.TotalMinutes), 1),
                JobCount = g.Count(),
            })
            .OrderBy(d => d.Date)
            .ToList();
    }

    public async Task<List<FailureReasonDto>> GetFailureReasonsAsync(int? days, CancellationToken ct = default)
    {
        var since = ComputeSince(days);
        var query = _db.Set<PrintJob>()
            .Where(j => j.Status == PrintJobStatus.Failed)
            .Where(j => j.FailureReason != null);

        if (since.HasValue)
        {
            query = query.Where(j => j.QueuedAt >= since.Value);
        }

        return await query
            .GroupBy(j => j.FailureReason!)
            .Select(g => new FailureReasonDto
            {
                Reason = g.Key,
                Count = g.Count(),
            })
            .OrderByDescending(f => f.Count)
            .ToListAsync(ct);
    }

    private static DateTime? ComputeSince(int? days)
    {
        return days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : null;
    }
}
