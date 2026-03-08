using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Service for exporting print statistics to PDF and CSV formats.
/// </summary>
public interface IReportExportService
{
    /// <summary>
    /// Generates a comprehensive print report in PDF format.
    /// </summary>
    Task<byte[]> GeneratePdfReportAsync(ReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates print job history CSV export.
    /// </summary>
    Task<byte[]> GenerateJobHistoryCsvAsync(ReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates cost breakdown CSV export.
    /// </summary>
    Task<byte[]> GenerateCostCsvAsync(ReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates printer utilization CSV export.
    /// </summary>
    Task<byte[]> GenerateUtilizationCsvAsync(ReportRequest request, CancellationToken ct = default);
}
