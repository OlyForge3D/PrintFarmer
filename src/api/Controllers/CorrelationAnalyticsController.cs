using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides performance correlation analytics for materials, printers, and settings.
/// </summary>
[ApiController]
[Route("api/correlation-analytics")]
[Authorize]
public class CorrelationAnalyticsController(ICorrelationAnalyticsService correlationService) : ControllerBase
{
    private readonly ICorrelationAnalyticsService _correlationService = correlationService;

    /// <summary>
    /// Returns success rate breakdown by material type.
    /// </summary>
    [HttpGet("material-success-rates")]
    [ProducesResponseType(typeof(List<MaterialSuccessRateDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaterialSuccessRatesAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetMaterialSuccessRatesAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns success rate breakdown by printer × material combination.
    /// </summary>
    [HttpGet("printer-material-performance")]
    [ProducesResponseType(typeof(List<PrinterMaterialPerformanceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinterMaterialPerformanceAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetPrinterMaterialPerformanceAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns temperature vs quality correlation data.
    /// </summary>
    [HttpGet("temperature-quality")]
    [ProducesResponseType(typeof(List<TemperatureQualityCorrelationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTemperatureQualityDataAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetTemperatureQualityDataAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns print duration trends over time.
    /// </summary>
    [HttpGet("duration-trends")]
    [ProducesResponseType(typeof(List<DurationTrendDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDurationTrendsAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetDurationTrendsAsync(days, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns failure reasons breakdown.
    /// </summary>
    [HttpGet("failure-reasons")]
    [ProducesResponseType(typeof(List<FailureReasonDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFailureReasonsAsync([FromQuery] int? days, CancellationToken ct)
    {
        var result = await _correlationService.GetFailureReasonsAsync(days, ct);
        return Ok(result);
    }
}
