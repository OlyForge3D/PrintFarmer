using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Provides aggregated statistics for dashboard visualization.
/// </summary>
public interface IStatisticsService
{
    /// <summary>
    /// Returns high-level KPI summary values for print jobs.
    /// </summary>
    /// <param name="days">Optional number of days to filter. If null, returns all-time stats.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StatisticsSummaryDto> GetSummaryAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns daily job counts grouped by status for chart display.
    /// </summary>
    /// <param name="days">Number of days to query (1-365).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<DailyJobCountDto>> GetJobsOverTimeAsync(int days, CancellationToken ct = default);

    /// <summary>
    /// Returns daily cost totals for cost-over-time chart.
    /// </summary>
    /// <param name="days">Number of days to query (1-365).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<DailyCostDto>> GetCostOverTimeAsync(int days, CancellationToken ct = default);

    /// <summary>
    /// Returns filament consumption grouped by material type.
    /// </summary>
    /// <param name="days">Optional number of days to filter. If null, returns all-time stats.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<FilamentByMaterialDto>> GetFilamentByMaterialAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns per-printer utilization stats.
    /// </summary>
    /// <param name="days">Optional number of days to filter. If null, returns all-time stats.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<PrinterUtilizationDto>> GetPrinterUtilizationAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns aggregate cost statistics summary.
    /// </summary>
    /// <param name="days">Optional number of days to filter. If null, returns all-time stats.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CostStatisticsSummaryDto> GetCostsSummaryAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns cost data grouped by time period.
    /// </summary>
    /// <param name="days">Optional number of days to filter. If null, returns all-time stats.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CostByTimePeriodDto>> GetCostsByTimePeriodAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns cost data grouped by printer.
    /// </summary>
    /// <param name="days">Optional number of days to filter. If null, returns all-time stats.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CostByPrinterDto>> GetCostsByPrinterAsync(int? days, CancellationToken ct = default);

    /// <summary>
    /// Returns cost data grouped by material type.
    /// </summary>
    /// <param name="days">Optional number of days to filter. If null, returns all-time stats.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CostByMaterialDto>> GetCostsByMaterialAsync(int? days, CancellationToken ct = default);
}
