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
    Task<List<MaintenanceForecastDto>> ForecastMaintenanceAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns active predictive alerts based on current data patterns.
    /// </summary>
    Task<List<PredictiveAlertDto>> GetActiveAlertsAsync(CancellationToken ct = default);
}
