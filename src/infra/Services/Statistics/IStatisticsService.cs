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

    /// <summary>
    /// Returns a keyset-paginated page of per-job cost breakdowns for completed jobs,
    /// ordered by completion time descending. The requested <paramref name="pageSize"/>
    /// is clamped server-side to <see cref="StatisticsService.MaxCostsByJobPageSize"/>
    /// regardless of caller input, so the response payload is always bounded.
    /// </summary>
    /// <param name="days">Number of trailing days to include when <paramref name="startDate"/> is not provided.</param>
    /// <param name="startDate">Optional inclusive start of the date range.</param>
    /// <param name="endDate">Optional inclusive end of the date range.</param>
    /// <param name="cursor">
    /// Opaque cursor returned as <c>NextCursor</c> from a previous call, or <c>null</c> to
    /// request the first page.
    /// </param>
    /// <param name="pageSize">
    /// Requested page size. Defaults to <see cref="StatisticsService.DefaultCostsByJobPageSize"/>
    /// and is clamped to <see cref="StatisticsService.MaxCostsByJobPageSize"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<CostByJobPageDto> GetCostsByJobAsync(
        int? days,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken ct = default);
}
