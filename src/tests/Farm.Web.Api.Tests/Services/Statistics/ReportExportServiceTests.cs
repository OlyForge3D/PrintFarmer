using System.Globalization;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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

    [Fact]
    public async Task GenerateJobHistoryCsvAsync_SpecialCharactersAndDates_PreservesCsvFormat()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext db = new(dbOptions);
        DateTime queuedAt = DateTime.UtcNow.Date.AddHours(10);
        db.PrintJobs.Add(new PrintJob
        {
            Name = " Job,\"one\"\nline ",
            QueuedAt = queuedAt,
            Status = PrintJobStatus.Completed,
            ActualPrintTime = TimeSpan.FromMinutes(90),
            ActualFilamentUsage = 4.5,
            ActualCost = 3.25m,
        });
        await db.SaveChangesAsync();
        var service = new ReportExportService(Mock.Of<IStatisticsService>(), db);

        string csv = Encoding.UTF8.GetString(await service.GenerateJobHistoryCsvAsync(new ReportRequest()));

        csv.Should().Be(
            "JobName,Status,QueuedAt,StartedAt,CompletedAt,PrintTimeMinutes,FilamentGrams,Cost,PrinterName\r\n" +
            $"\" Job,\"\"one\"\"\nline \",Completed,{queuedAt.ToString("G", CultureInfo.InvariantCulture)},,,90,4.5,3.25,\r\n");
    }

    [Fact]
    public async Task GenerateStatisticsCsvAsync_PopulatedRecords_PreservesHeadersAndValues()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext db = new(dbOptions);
        Guid printerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Mock<IStatisticsService> statistics = new();
        statistics.Setup(service => service.GetCostOverTimeAsync(30, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DailyCostDto { Date = "2026-01-02", Cost = 12.5m }]);
        statistics.Setup(service => service.GetPrinterUtilizationAsync(30, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PrinterUtilizationDto
            {
                PrinterId = printerId,
                PrinterName = " Printer ",
                TotalJobs = 3,
                CompletedJobs = 2,
                FailedJobs = 1,
                TotalPrintHours = 4.5,
                SuccessRate = 66.5,
            }]);
        var exporter = new ReportExportService(statistics.Object, db);

        string costs = Encoding.UTF8.GetString(await exporter.GenerateCostCsvAsync(new ReportRequest { Days = 30 }));
        string utilization = Encoding.UTF8.GetString(await exporter.GenerateUtilizationCsvAsync(new ReportRequest { Days = 30 }));

        costs.Should().Be("Date,Cost\r\n2026-01-02,12.5\r\n");
        utilization.Should().Be(
            $"PrinterId,PrinterName,TotalJobs,CompletedJobs,FailedJobs,TotalPrintHours,SuccessRate\r\n{printerId},\" Printer \",3,2,1,4.5,66.5\r\n");
    }

    [Fact]
    public async Task GenerateStatisticsCsvAsync_EmptyRecords_StillWritesHeaders()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext db = new(dbOptions);
        Mock<IStatisticsService> statistics = new();
        statistics.Setup(service => service.GetCostOverTimeAsync(30, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        statistics.Setup(service => service.GetPrinterUtilizationAsync(30, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var exporter = new ReportExportService(statistics.Object, db);

        string costs = Encoding.UTF8.GetString(await exporter.GenerateCostCsvAsync(new ReportRequest { Days = 30 }));
        string utilization = Encoding.UTF8.GetString(await exporter.GenerateUtilizationCsvAsync(new ReportRequest { Days = 30 }));

        costs.Should().Be("Date,Cost\r\n");
        utilization.Should().Be("PrinterId,PrinterName,TotalJobs,CompletedJobs,FailedJobs,TotalPrintHours,SuccessRate\r\n");
    }
}
