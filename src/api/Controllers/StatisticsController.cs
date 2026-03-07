using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides aggregated print statistics for dashboard visualisation.
/// </summary>
[ApiController]
[Route("api/statistics")]
[Authorize]
public class StatisticsController(IStatisticsService statisticsService) : ControllerBase
{
    private readonly IStatisticsService _statisticsService = statisticsService;

    /// <summary>
    /// Returns high-level KPI summary values.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(StatisticsSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummaryAsync([FromQuery] int? days, CancellationToken ct)
    {
        var summary = await _statisticsService.GetSummaryAsync(days, ct);
        return Ok(summary);
    }

    /// <summary>
    /// Returns daily job counts grouped by status for chart display.
    /// </summary>
    [HttpGet("jobs-over-time")]
    [ProducesResponseType(typeof(List<DailyJobCountDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobsOverTimeAsync([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var result = await _statisticsService.GetJobsOverTimeAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns daily cost totals for cost-over-time chart.
    /// </summary>
    [HttpGet("cost-over-time")]
    [ProducesResponseType(typeof(List<DailyCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCostOverTimeAsync([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var result = await _statisticsService.GetCostOverTimeAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns filament consumption grouped by material type.
    /// </summary>
    [HttpGet("filament-by-material")]
    [ProducesResponseType(typeof(List<FilamentByMaterialDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFilamentByMaterialAsync([FromQuery] int? days, CancellationToken ct = default)
    {
        var result = await _statisticsService.GetFilamentByMaterialAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns per-printer utilisation stats.
    /// </summary>
    [HttpGet("printer-utilization")]
    [ProducesResponseType(typeof(List<PrinterUtilizationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinterUtilizationAsync([FromQuery] int? days, CancellationToken ct = default)
    {
        var result = await _statisticsService.GetPrinterUtilizationAsync(days, ct);
        return Ok(result);
    }
}
