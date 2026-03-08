using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for computing performance correlations across materials, printers, and print settings.
/// </summary>
public interface ICorrelationAnalyticsService
{
    /// <summary>
    /// Returns success rate breakdown by material type.
    /// </summary>
    Task<List<MaterialSuccessRateDto>> GetMaterialSuccessRatesAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns success rate breakdown by printer × material combination.
    /// </summary>
    Task<List<PrinterMaterialPerformanceDto>> GetPrinterMaterialPerformanceAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns temperature vs quality correlation data (completed jobs only).
    /// </summary>
    Task<List<TemperatureQualityCorrelationDto>> GetTemperatureQualityDataAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns print duration distribution over time.
    /// </summary>
    Task<List<DurationTrendDto>> GetDurationTrendsAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns failure reasons breakdown.
    /// </summary>
    Task<List<FailureReasonDto>> GetFailureReasonsAsync(int? days, CancellationToken ct = default);
}
