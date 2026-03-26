using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Provides aggregated statistics for dashboard visualization.
/// All methods support optional custom date ranges via startDate/endDate.
/// When provided, custom dates take precedence over days.
/// </summary>
public interface IStatisticsService
{
    /// <summary>
    /// Returns high-level KPI summary values for print jobs.
    /// </summary>
    Task<StatisticsSummaryDto> GetSummaryAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns daily job counts grouped by status for chart display. Defaults to 30 days.
    /// </summary>
    Task<List<DailyJobCountDto>> GetJobsOverTimeAsync(int? days = null, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns daily cost totals for cost-over-time chart. Defaults to 30 days.
    /// </summary>
    Task<List<DailyCostDto>> GetCostOverTimeAsync(int? days = null, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns filament consumption grouped by material type.
    /// </summary>
    Task<List<FilamentByMaterialDto>> GetFilamentByMaterialAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns per-printer utilization stats.
    /// </summary>
    Task<List<PrinterUtilizationDto>> GetPrinterUtilizationAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns aggregate cost statistics summary.
    /// </summary>
    Task<CostStatisticsSummaryDto> GetCostsSummaryAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns cost data grouped by time period. Defaults to 30 days.
    /// </summary>
    Task<List<CostByTimePeriodDto>> GetCostsByTimePeriodAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns cost data grouped by printer.
    /// </summary>
    Task<List<CostByPrinterDto>> GetCostsByPrinterAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);

    /// <summary>
    /// Returns cost data grouped by material type.
    /// </summary>
    Task<List<CostByMaterialDto>> GetCostsByMaterialAsync(int? days, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);
}
