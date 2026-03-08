using System.Globalization;
using CsvHelper;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Generates PDF and CSV report exports from print statistics data.
/// </summary>
public class ReportExportService(
    IStatisticsService statisticsService,
    AppDbContext db) : IReportExportService
{
    private readonly IStatisticsService _statisticsService = statisticsService;
    private readonly AppDbContext _db = db;

    public async Task<byte[]> GeneratePdfReportAsync(ReportRequest request, CancellationToken ct = default)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var summary = await _statisticsService.GetSummaryAsync(request.Days, ct)
            ?? new StatisticsSummaryDto();
        var utilization = await _statisticsService.GetPrinterUtilizationAsync(request.Days, ct)
            ?? [];
        var filament = await _statisticsService.GetFilamentByMaterialAsync(request.Days, ct)
            ?? [];

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(50);
                page.Header().Element(ComposeHeader);
                page.Content().Element(content => ComposeContent(content, summary, utilization, filament));
                page.Footer().AlignCenter().Text(text =>
                {
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    public async Task<byte[]> GenerateJobHistoryCsvAsync(ReportRequest request, CancellationToken ct = default)
    {
        var days = request.Days ?? 365;
        var since = DateTime.UtcNow.AddDays(-days);

        var jobs = await _db.Set<PrintJob>()
            .Where(j => j.QueuedAt >= since)
            .OrderByDescending(j => j.QueuedAt)
            .Take(10000)
            .Select(j => new JobHistoryCsvRow
            {
                JobName = j.Name,
                Status = j.Status.ToString(),
                QueuedAt = j.QueuedAt,
                StartedAt = j.ActualStartTime,
                CompletedAt = j.ActualEndTime,
                PrintTimeMinutes = j.ActualPrintTime.HasValue
                    ? j.ActualPrintTime.Value.TotalMinutes
                    : null,
                FilamentGrams = j.ActualFilamentUsage,
                Cost = j.ActualCost,
                PrinterName = j.AssignedPrinter != null ? j.AssignedPrinter.Name : null,
            })
            .ToListAsync(ct);

        return WriteCsv(jobs);
    }

    public async Task<byte[]> GenerateCostCsvAsync(ReportRequest request, CancellationToken ct = default)
    {
        int days = request.Days ?? 365;
        var costData = await _statisticsService.GetCostOverTimeAsync(days, ct);
        return WriteCsv(costData);
    }

    public async Task<byte[]> GenerateUtilizationCsvAsync(ReportRequest request, CancellationToken ct = default)
    {
        var utilization = await _statisticsService.GetPrinterUtilizationAsync(request.Days, ct);
        return WriteCsv(utilization);
    }

    private static byte[] WriteCsv<T>(IEnumerable<T> records)
    {
        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteRecords(records);
        writer.Flush();
        return memoryStream.ToArray();
    }

    private static void ComposeHeader(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("PrintFarmer Analytics Report").FontSize(20).Bold();
                column.Item().Text($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC").FontSize(10);
            });
        });
    }

    private static void ComposeContent(
        IContainer container,
        StatisticsSummaryDto summary,
        List<PrinterUtilizationDto> utilization,
        List<FilamentByMaterialDto> filament)
    {
        container.PaddingVertical(20).Column(column =>
        {
            column.Item().Text("Summary Statistics").FontSize(16).Bold();
            column.Item().PaddingTop(10).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                });

                table.Cell().Text("Total Jobs").Bold();
                table.Cell().Text(summary.TotalJobs.ToString());
                table.Cell().Text("Completed").Bold();
                table.Cell().Text(summary.CompletedJobs.ToString());
                table.Cell().Text("Failed").Bold();
                table.Cell().Text(summary.FailedJobs.ToString());
                table.Cell().Text("Success Rate").Bold();
                table.Cell().Text($"{summary.SuccessRate}%");
                table.Cell().Text("Total Cost").Bold();
                table.Cell().Text($"${summary.TotalCost:N2}");
                table.Cell().Text("Total Print Hours").Bold();
                table.Cell().Text($"{summary.TotalPrintHours:N1}");
                table.Cell().Text("Filament Used (g)").Bold();
                table.Cell().Text($"{summary.TotalFilamentGrams:N1}");
            });

            if (utilization.Count > 0)
            {
                column.Item().PaddingTop(20).Text("Printer Utilization").FontSize(16).Bold();
                column.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(3);
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });

                    table.Header(header =>
                    {
                        header.Cell().Text("Printer").Bold();
                        header.Cell().Text("Total Jobs").Bold();
                        header.Cell().Text("Completed").Bold();
                        header.Cell().Text("Failed").Bold();
                        header.Cell().Text("Success Rate").Bold();
                    });

                    foreach (var printer in utilization)
                    {
                        table.Cell().Text(printer.PrinterName);
                        table.Cell().Text(printer.TotalJobs.ToString());
                        table.Cell().Text(printer.CompletedJobs.ToString());
                        table.Cell().Text(printer.FailedJobs.ToString());
                        table.Cell().Text($"{printer.SuccessRate}%");
                    }
                });
            }

            if (filament.Count > 0)
            {
                column.Item().PaddingTop(20).Text("Filament Consumption by Material").FontSize(16).Bold();
                column.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(2);
                        columns.RelativeColumn();
                    });

                    table.Header(header =>
                    {
                        header.Cell().Text("Material").Bold();
                        header.Cell().Text("Grams Used").Bold();
                    });

                    foreach (var material in filament)
                    {
                        table.Cell().Text(material.Material);
                        table.Cell().Text($"{material.Grams:N1}");
                    }
                });
            }
        });
    }
}
