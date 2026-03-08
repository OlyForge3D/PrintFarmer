using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Statistics;

public class ReportExportServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ReportExportServiceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GeneratePdfReportAsync_WithNoJobs_ReturnsNonEmptyPdf()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IReportExportService>();

        var pdf = await service.GeneratePdfReportAsync(new ReportRequest { Days = 30 });

        pdf.Should().NotBeNull();
        pdf.Should().NotBeEmpty();
        pdf.Take(4).Should().BeEquivalentTo(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF"
    }

    [Fact]
    public async Task GeneratePdfReportAsync_WithJobData_ReturnsNonEmptyPdf()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IReportExportService>();

        var manufacturer = new Manufacturer { Name = "PdfTestMfg" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "PdfTestModel",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Name = "PdfTestPrinter",
            ServerUrl = "http://pdftest.local",
            BackendPort = 7125,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var job = new PrintJob
        {
            Name = "PdfTestJob",
            QueuedAt = DateTime.UtcNow.AddDays(-1),
            Status = PrintJobStatus.Completed,
            AssignedPrinterId = printer.Id,
            ActualCost = 5.50m,
            ActualFilamentUsage = 100.0,
            ActualPrintTime = TimeSpan.FromHours(2)
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();

        var pdf = await service.GeneratePdfReportAsync(new ReportRequest { Days = 30 });

        pdf.Should().NotBeNull();
        pdf.Should().NotBeEmpty();
        pdf.Take(4).Should().BeEquivalentTo(new byte[] { 0x25, 0x50, 0x44, 0x46 });
    }

    [Fact]
    public async Task GenerateJobHistoryCsvAsync_WithNoJobs_ReturnsValidEmptyCsv()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IReportExportService>();

        var csv = await service.GenerateJobHistoryCsvAsync(new ReportRequest { Days = 30 });

        csv.Should().NotBeNull();
        csv.Should().NotBeEmpty();
        var csvText = System.Text.Encoding.UTF8.GetString(csv);
        csvText.Should().Contain("JobName");
    }

    [Fact]
    public async Task GenerateJobHistoryCsvAsync_WithJobs_ReturnsPopulatedCsv()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IReportExportService>();

        var manufacturer = new Manufacturer { Name = "CsvMfg2" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "CsvModel2",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Name = "CsvPrinter2",
            ServerUrl = "http://csv2.local",
            BackendPort = 7125,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var job = new PrintJob
        {
            Name = "CsvTestJob",
            QueuedAt = DateTime.UtcNow.AddDays(-1),
            Status = PrintJobStatus.Completed,
            AssignedPrinterId = printer.Id,
            ActualCost = 3.25m,
            ActualFilamentUsage = 50.0,
            ActualPrintTime = TimeSpan.FromHours(1)
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();

        var csv = await service.GenerateJobHistoryCsvAsync(new ReportRequest { Days = 30 });

        csv.Should().NotBeNull();
        var csvText = System.Text.Encoding.UTF8.GetString(csv);
        csvText.Should().Contain("CsvTestJob");
        csvText.Should().Contain("Completed");
    }

    [Fact]
    public async Task GenerateJobHistoryCsvAsync_WithDateRangeFilter_ReturnsFilteredData()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IReportExportService>();

        var manufacturer = new Manufacturer { Name = "DateMfg2" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "DateModel2",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Name = "DatePrinter2",
            ServerUrl = "http://date2.local",
            BackendPort = 7125,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var oldJob = new PrintJob
        {
            Name = "RptOldJob",
            QueuedAt = DateTime.UtcNow.AddDays(-100),
            Status = PrintJobStatus.Completed,
            AssignedPrinterId = printer.Id
        };
        var recentJob = new PrintJob
        {
            Name = "RptRecentJob",
            QueuedAt = DateTime.UtcNow.AddDays(-5),
            Status = PrintJobStatus.Completed,
            AssignedPrinterId = printer.Id
        };
        db.PrintJobs.AddRange(oldJob, recentJob);
        await db.SaveChangesAsync();

        var csv = await service.GenerateJobHistoryCsvAsync(new ReportRequest { Days = 30 });

        var csvText = System.Text.Encoding.UTF8.GetString(csv);
        csvText.Should().Contain("RptRecentJob");
        csvText.Should().NotContain("RptOldJob");
    }

    [Fact]
    public async Task GenerateCostCsvAsync_WithNoData_ReturnsValidCsv()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IReportExportService>();

        var csv = await service.GenerateCostCsvAsync(new ReportRequest { Days = 30 });

        csv.Should().NotBeNull();
        csv.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateUtilizationCsvAsync_WithNoData_ReturnsValidCsv()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IReportExportService>();

        var csv = await service.GenerateUtilizationCsvAsync(new ReportRequest { Days = 30 });

        csv.Should().NotBeNull();
        csv.Should().NotBeEmpty();
    }
}
