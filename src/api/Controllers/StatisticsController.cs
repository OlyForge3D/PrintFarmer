using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides aggregated print statistics for dashboard visualisation.
/// All endpoints support optional startDate/endDate query parameters for custom date ranges.
/// When provided, startDate/endDate take precedence over the days parameter.
/// </summary>
/// <remarks>
/// Validation: startDate must be before endDate, max range is 730 days (2 years).
/// </remarks>
[ApiController]
[Route("api/statistics")]
[Authorize]
public class StatisticsController(IStatisticsService statisticsService) : ControllerBase
{
    private const int MaxDateRangeDays = 730;
    private readonly IStatisticsService _statisticsService = statisticsService;

    /// <summary>
    /// Validates that startDate/endDate form a valid range.
    /// Returns a BadRequest result if invalid, or null if valid.
    /// </summary>
    private static BadRequestObjectResult? ValidateDateRange(DateTime? startDate, DateTime? endDate)
    {
        if (startDate.HasValue && endDate.HasValue)
        {
            if (startDate.Value > endDate.Value)
            {
                return new BadRequestObjectResult(new { error = "startDate must be before endDate" });
            }

            if ((endDate.Value - startDate.Value).TotalDays > MaxDateRangeDays)
            {
                return new BadRequestObjectResult(new { error = $"Date range cannot exceed {MaxDateRangeDays} days (2 years)" });
            }
        }

        return null;
    }

    /// <summary>
    /// Returns high-level KPI summary values.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(StatisticsSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetSummaryAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var summary = await _statisticsService.GetSummaryAsync(days, startDate, endDate, ct);
        return Ok(summary);
    }

    /// <summary>
    /// Returns daily job counts grouped by status for chart display.
    /// </summary>
    [HttpGet("jobs-over-time")]
    [ProducesResponseType(typeof(List<DailyJobCountDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetJobsOverTimeAsync(
        [FromQuery] int? days = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetJobsOverTimeAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns daily cost totals for cost-over-time chart.
    /// </summary>
    [HttpGet("cost-over-time")]
    [ProducesResponseType(typeof(List<DailyCostDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCostOverTimeAsync(
        [FromQuery] int? days = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetCostOverTimeAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns filament consumption grouped by material type.
    /// </summary>
    [HttpGet("filament-by-material")]
    [ProducesResponseType(typeof(List<FilamentByMaterialDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetFilamentByMaterialAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetFilamentByMaterialAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns per-printer utilisation stats.
    /// </summary>
    [HttpGet("printer-utilization")]
    [ProducesResponseType(typeof(List<PrinterUtilizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPrinterUtilizationAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetPrinterUtilizationAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns aggregate cost statistics summary.
    /// </summary>
    [HttpGet("costs/summary")]
    [ProducesResponseType(typeof(CostStatisticsSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCostsSummaryAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetCostsSummaryAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns cost data grouped by time period.
    /// </summary>
    [HttpGet("costs")]
    [ProducesResponseType(typeof(List<CostByTimePeriodDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCostsByTimePeriodAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetCostsByTimePeriodAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns cost data grouped by printer.
    /// </summary>
    [HttpGet("costs/by-printer")]
    [ProducesResponseType(typeof(List<CostByPrinterDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCostsByPrinterAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetCostsByPrinterAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns cost data grouped by material type.
    /// </summary>
    [HttpGet("costs/by-material")]
    [ProducesResponseType(typeof(List<CostByMaterialDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCostsByMaterialAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetCostsByMaterialAsync(days, startDate, endDate, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns per-job cost breakdowns for completed print jobs.
    /// </summary>
    [HttpGet("costs/by-job")]
    [ProducesResponseType(typeof(List<CostByJobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCostsByJobAsync(
        [FromQuery] int? days,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        IActionResult? validationError = ValidateDateRange(startDate, endDate);
        if (validationError is not null)
        {
            return validationError;
        }

        var result = await _statisticsService.GetCostsByJobAsync(days, startDate, endDate, ct);
        return Ok(result);
    }
}
