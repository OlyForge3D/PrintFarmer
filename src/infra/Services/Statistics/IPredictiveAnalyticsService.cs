using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for predictive analytics and alert generation based on historical patterns.
/// </summary>
public interface IPredictiveAnalyticsService
{
    /// <summary>
    /// Calculates job failure likelihood based on historical patterns.
    /// </summary>
    Task<JobFailurePredictionDto> PredictJobFailureLikelihoodAsync(PredictionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Forecasts printer maintenance needs based on usage patterns.
    /// </summary>
    /// <param name="days">Number of days to forecast ahead.</param>
    /// <param name="printerId">Optional printer ID to scope the forecast to a single printer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<MaintenanceForecastDto>> ForecastMaintenanceAsync(int? days, Guid? printerId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns active predictive alerts based on current data patterns.
    /// </summary>
    /// <param name="printerId">Optional printer ID to scope alerts to a single printer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<PredictiveAlertDto>> GetActiveAlertsAsync(Guid? printerId = null, CancellationToken ct = default);
}
