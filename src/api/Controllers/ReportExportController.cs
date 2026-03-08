using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides PDF and CSV report export endpoints for print statistics.
/// </summary>
[ApiController]
[Route("api/statistics/export")]
[Authorize]
public class ReportExportController(IReportExportService reportExportService) : ControllerBase
{
    private readonly IReportExportService _reportExportService = reportExportService;

    /// <summary>
    /// Exports comprehensive print report as PDF.
    /// </summary>
    [HttpGet("pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportPdfReportAsync([FromQuery] int? days, CancellationToken ct)
    {
        var report = await _reportExportService.GeneratePdfReportAsync(
            new ReportRequest { Days = days }, ct);

        var fileName = $"printfarmer-report-{DateTime.UtcNow:yyyy-MM-dd}.pdf";
        return File(report, "application/pdf", fileName);
    }

    /// <summary>
    /// Exports job history as CSV.
    /// </summary>
    [HttpGet("jobs-csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportJobHistoryCsvAsync([FromQuery] int? days, CancellationToken ct)
    {
        var csv = await _reportExportService.GenerateJobHistoryCsvAsync(
            new ReportRequest { Days = days }, ct);

        var fileName = $"job-history-{DateTime.UtcNow:yyyy-MM-dd}.csv";
        return File(csv, "text/csv", fileName);
    }

    /// <summary>
    /// Exports cost data as CSV.
    /// </summary>
    [HttpGet("cost-csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportCostCsvAsync([FromQuery] int? days, CancellationToken ct)
    {
        var csv = await _reportExportService.GenerateCostCsvAsync(
            new ReportRequest { Days = days }, ct);

        var fileName = $"cost-breakdown-{DateTime.UtcNow:yyyy-MM-dd}.csv";
        return File(csv, "text/csv", fileName);
    }

    /// <summary>
    /// Exports printer utilization as CSV.
    /// </summary>
    [HttpGet("utilization-csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportUtilizationCsvAsync([FromQuery] int? days, CancellationToken ct)
    {
        var csv = await _reportExportService.GenerateUtilizationCsvAsync(
            new ReportRequest { Days = days }, ct);

        var fileName = $"printer-utilization-{DateTime.UtcNow:yyyy-MM-dd}.csv";
        return File(csv, "text/csv", fileName);
    }
}
