using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides aggregated print statistics for dashboard visualisation.
/// </summary>
[ApiController]
[Route("api/statistics")]
[Authorize]
public class StatisticsController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Returns high-level KPI summary values.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(StatisticsSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummaryAsync([FromQuery] int? days, CancellationToken ct)
    {
        int effectiveDays = days.HasValue ? Math.Clamp(days.Value, 1, 365) : 0;
        var since = effectiveDays > 0 ? DateTime.UtcNow.AddDays(-effectiveDays) : (DateTime?)null;

        var query = db.Set<Farm.Infrastructure.Domain.PrintJob>().AsQueryable();
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

        double totalPrintHours = await query
            .Where(j => j.ActualPrintTime.HasValue)
            .SumAsync(j => j.ActualPrintTime!.Value.TotalHours, ct);

        int finishedJobs = completed + failed + cancelled;
        double successRate = finishedJobs > 0 ? (double)completed / finishedJobs * 100 : 0;

        return Ok(new StatisticsSummaryDto
        {
            TotalJobs = totalJobs,
            CompletedJobs = completed,
            FailedJobs = failed,
            CancelledJobs = cancelled,
            SuccessRate = Math.Round(successRate, 1),
            TotalCost = totalCost,
            TotalFilamentGrams = Math.Round(totalFilamentGrams, 1),
            TotalPrintHours = Math.Round(totalPrintHours, 1),
        });
    }

    /// <summary>
    /// Returns daily job counts grouped by status for chart display.
    /// </summary>
    [HttpGet("jobs-over-time")]
    [ProducesResponseType(typeof(List<DailyJobCountDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobsOverTimeAsync([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        var rows = await db.Set<Farm.Infrastructure.Domain.PrintJob>()
            .Where(j => j.QueuedAt >= since)
            .Where(j => j.Status == PrintJobStatus.Completed
                     || j.Status == PrintJobStatus.Failed
                     || j.Status == PrintJobStatus.Cancelled)
            .GroupBy(j => new { j.QueuedAt.Date, j.Status })
            .Select(g => new { g.Key.Date, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        // Build complete date range with zeros
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

        return Ok(result);
    }

    /// <summary>
    /// Returns daily cost totals for cost-over-time chart.
    /// </summary>
    [HttpGet("cost-over-time")]
    [ProducesResponseType(typeof(List<DailyCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCostOverTimeAsync([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        var rows = await db.Set<Farm.Infrastructure.Domain.PrintJob>()
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

        return Ok(result);
    }

    /// <summary>
    /// Returns filament consumption grouped by material type.
    /// </summary>
    [HttpGet("filament-by-material")]
    [ProducesResponseType(typeof(List<FilamentByMaterialDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFilamentByMaterialAsync([FromQuery] int? days, CancellationToken ct = default)
    {
        int? clampedDays = days.HasValue ? Math.Clamp(days.Value, 1, 365) : null;
        var query = db.Set<Farm.Infrastructure.Domain.PrintJob>()
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
                Grams = Math.Round(g.Sum(j => j.ActualFilamentUsage!.Value), 1),
            })
            .OrderByDescending(r => r.Grams)
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>
    /// Returns per-printer utilisation stats.
    /// </summary>
    [HttpGet("printer-utilization")]
    [ProducesResponseType(typeof(List<PrinterUtilizationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinterUtilizationAsync([FromQuery] int? days, CancellationToken ct = default)
    {
        int? clampedDays = days.HasValue ? Math.Clamp(days.Value, 1, 365) : null;
        var query = db.Set<Farm.Infrastructure.Domain.PrintJob>()
            .Where(j => j.AssignedPrinterId.HasValue);

        if (clampedDays.HasValue)
        {
            var since = DateTime.UtcNow.AddDays(-clampedDays.Value);
            query = query.Where(j => j.QueuedAt >= since);
        }

        var rows = await query
            .GroupBy(j => new { PrinterId = j.AssignedPrinterId!.Value })
            .Select(g => new
            {
                PrinterId = g.Key.PrinterId,
                TotalJobs = g.Count(),
                Completed = g.Count(j => j.Status == PrintJobStatus.Completed),
                Failed = g.Count(j => j.Status == PrintJobStatus.Failed),
                TotalHours = g.Where(j => j.ActualPrintTime.HasValue)
                    .Sum(j => j.ActualPrintTime!.Value.TotalHours),
            })
            .ToListAsync(ct);

        // Get printer names
        var printerIds = rows.Select(r => r.PrinterId).ToList();
        var printerNames = await db.Printers
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

        return Ok(result);
    }
}

// ── Response DTOs ──────────────────────────────────────────────
public record StatisticsSummaryDto
{
    public int TotalJobs { get; init; }

    public int CompletedJobs { get; init; }

    public int FailedJobs { get; init; }

    public int CancelledJobs { get; init; }

    public double SuccessRate { get; init; }

    public decimal TotalCost { get; init; }

    public double TotalFilamentGrams { get; init; }

    public double TotalPrintHours { get; init; }
}

public record DailyJobCountDto
{
    public string Date { get; init; } = string.Empty;

    public int Completed { get; init; }

    public int Failed { get; init; }

    public int Cancelled { get; init; }
}

public record DailyCostDto
{
    public string Date { get; init; } = string.Empty;

    public decimal Cost { get; init; }
}

public record FilamentByMaterialDto
{
    public string Material { get; init; } = string.Empty;

    public double Grams { get; init; }
}

public record PrinterUtilizationDto
{
    public Guid PrinterId { get; init; }

    public string PrinterName { get; init; } = string.Empty;

    public int TotalJobs { get; init; }

    public int CompletedJobs { get; init; }

    public int FailedJobs { get; init; }

    public double TotalPrintHours { get; init; }

    public double SuccessRate { get; init; }
}
