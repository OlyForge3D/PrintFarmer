using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides predictive analytics and alert generation endpoints.
/// </summary>
[ApiController]
[Route("api/predictive-analytics")]
[Authorize]
public class PredictiveAnalyticsController(IPredictiveAnalyticsService predictiveService) : ControllerBase
{
    private readonly IPredictiveAnalyticsService _predictiveService = predictiveService;

    /// <summary>
    /// Predicts job failure likelihood based on historical patterns.
    /// </summary>
    [HttpPost("predict-job-failure")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(JobFailurePredictionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> PredictJobFailureAsync(
        [FromBody] PredictionRequest request, CancellationToken ct)
    {
        var prediction = await _predictiveService.PredictJobFailureLikelihoodAsync(request, ct);
        return Ok(prediction);
    }

    /// <summary>
    /// Forecasts printer maintenance needs.
    /// </summary>
    [HttpGet("maintenance-forecast")]
    [ProducesResponseType(typeof(List<MaintenanceForecastDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaintenanceForecastAsync([FromQuery] int? days, CancellationToken ct)
    {
        var forecast = await _predictiveService.ForecastMaintenanceAsync(days, ct);
        return Ok(forecast);
    }

    /// <summary>
    /// Returns active predictive alerts.
    /// </summary>
    [HttpGet("active-alerts")]
    [ProducesResponseType(typeof(List<PredictiveAlertDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveAlertsAsync(CancellationToken ct)
    {
        var alerts = await _predictiveService.GetActiveAlertsAsync(ct);
        return Ok(alerts);
    }
}
