using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Statistics;

public class CorrelationAnalyticsServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CorrelationAnalyticsServiceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetMaterialSuccessRatesAsync_WithNoJobs_ReturnsEmptyList()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        var result = await service.GetMaterialSuccessRatesAsync(30);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetMaterialSuccessRatesAsync_WithJobs_ReturnsCorrectGroupings()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        var manufacturer = new Manufacturer { Name = "MatMfg" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "MatModel",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Name = "MatPrinter",
            ServerUrl = "http://mat.local",
            BackendPort = 7125,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        db.PrintJobs.AddRange(
            new PrintJob { Name = "CorrJob1", RequiredMaterialType = "PLA", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Completed, AssignedPrinterId = printer.Id },
            new PrintJob { Name = "CorrJob2", RequiredMaterialType = "PLA", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Completed, AssignedPrinterId = printer.Id },
            new PrintJob { Name = "CorrJob3", RequiredMaterialType = "PLA", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Failed, AssignedPrinterId = printer.Id }
        );
        await db.SaveChangesAsync();

        var result = await service.GetMaterialSuccessRatesAsync(30);

        result.Should().Contain(r => r.Material == "PLA");
        var pla = result.First(r => r.Material == "PLA");
        pla.TotalJobs.Should().BeGreaterThanOrEqualTo(3);
        pla.CompletedJobs.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetPrinterMaterialPerformanceAsync_WithMultiplePrinters_ReturnsCorrectBreakdown()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        var manufacturer = new Manufacturer { Name = "PerfMfg" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "PerfModel",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer1 = new Printer { Name = "CorrPrinter1", ServerUrl = "http://p1.local", BackendPort = 7125, ModelId = model.Id, ManufacturerId = manufacturer.Id, Backend = (int)PrinterBackend.Moonraker };
        var printer2 = new Printer { Name = "CorrPrinter2", ServerUrl = "http://p2.local", BackendPort = 7125, ModelId = model.Id, ManufacturerId = manufacturer.Id, Backend = (int)PrinterBackend.Moonraker };
        db.Printers.AddRange(printer1, printer2);
        await db.SaveChangesAsync();

        db.PrintJobs.Add(new PrintJob { Name = "CP1PLA", RequiredMaterialType = "PLA", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Completed, AssignedPrinterId = printer1.Id });
        db.PrintJobs.AddRange(
            new PrintJob { Name = "CP2PLA1", RequiredMaterialType = "PLA", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Completed, AssignedPrinterId = printer2.Id },
            new PrintJob { Name = "CP2PLA2", RequiredMaterialType = "PLA", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Failed, AssignedPrinterId = printer2.Id }
        );
        await db.SaveChangesAsync();

        var result = await service.GetPrinterMaterialPerformanceAsync(30);

        result.Should().Contain(r => r.PrinterName == "CorrPrinter1");
        result.Should().Contain(r => r.PrinterName == "CorrPrinter2");
    }

    [Fact]
    public async Task GetTemperatureQualityDataAsync_WithNoTemperatureData_ReturnsEmptyList()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        var result = await service.GetTemperatureQualityDataAsync(30);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDurationTrendsAsync_WithNoJobs_ReturnsEmptyList()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        var result = await service.GetDurationTrendsAsync(30);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDurationTrendsAsync_WithJobs_AggregatesByDate()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        var today = DateTime.UtcNow.Date;
        db.PrintJobs.AddRange(
            new PrintJob { Name = "DurJob1", QueuedAt = today.AddHours(1), Status = PrintJobStatus.Completed, ActualPrintTime = TimeSpan.FromHours(2) },
            new PrintJob { Name = "DurJob2", QueuedAt = today.AddHours(3), Status = PrintJobStatus.Completed, ActualPrintTime = TimeSpan.FromHours(3) }
        );
        await db.SaveChangesAsync();

        var result = await service.GetDurationTrendsAsync(30);

        result.Should().Contain(r => r.Date == today.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task GetFailureReasonsAsync_WithNoFailures_ReturnsEmptyList()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        var result = await service.GetFailureReasonsAsync(30);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetFailureReasonsAsync_WithFailedJobs_GroupsByReason()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICorrelationAnalyticsService>();

        db.PrintJobs.AddRange(
            new PrintJob { Name = "CorrFail1", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Failed, FailureReason = "CorrAdhesion" },
            new PrintJob { Name = "CorrFail2", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Failed, FailureReason = "CorrAdhesion" },
            new PrintJob { Name = "CorrFail3", QueuedAt = DateTime.UtcNow, Status = PrintJobStatus.Failed, FailureReason = "CorrStringing" }
        );
        await db.SaveChangesAsync();

        var result = await service.GetFailureReasonsAsync(30);

        result.Should().Contain(r => r.Reason == "CorrAdhesion" && r.Count >= 2);
        result.Should().Contain(r => r.Reason == "CorrStringing" && r.Count >= 1);
    }
}
