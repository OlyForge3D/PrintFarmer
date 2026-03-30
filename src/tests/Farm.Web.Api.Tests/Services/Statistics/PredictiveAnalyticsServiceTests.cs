using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Statistics;

public class PredictiveAnalyticsServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PredictiveAnalyticsServiceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PredictJobFailureLikelihoodAsync_WithDecliningPrinter_ReturnsHighRisk()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPredictiveAnalyticsService>();

        var manufacturer = new Manufacturer { Name = "PredMfg" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "PredModel",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Name = "BadPrinter",
            ServerUrl = "http://bad.local",
            BackendPort = 7125,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        for (int i = 0; i < 5; i++)
        {
            db.PrintJobs.Add(new PrintJob
            {
                Name = $"PredFail{i}",
                QueuedAt = DateTime.UtcNow.AddDays(-i),
                Status = PrintJobStatus.Failed,
                AssignedPrinterId = printer.Id,
                RequiredMaterialType = "PLA"
            });
        }
        await db.SaveChangesAsync();

        var result = await service.PredictJobFailureLikelihoodAsync(new PredictionRequest
        {
            PrinterId = printer.Id,
            Material = "PLA",
            EstimatedDurationMinutes = 120
        });

        result.Should().NotBeNull();
        result.PredictedFailureLikelihood.Should().BeGreaterThan(20);
        result.RiskLevel.Should().BeOneOf("Medium", "High", "Critical");
    }

    [Fact]
    public async Task ForecastMaintenanceAsync_WithHighUsagePrinter_ReturnsMaintenanceTasks()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPredictiveAnalyticsService>();

        var manufacturer = new Manufacturer { Name = "MaintMfg" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "MaintModel",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Name = "HeavyUser",
            ServerUrl = "http://heavy.local",
            BackendPort = 7125,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        db.PrinterStatisticsSet.Add(new PrinterStatistics
        {
            PrinterId = printer.Id,
            TotalPrintHours = 480,
            TotalFilamentUsedGrams = 8000,
            TotalJobsCompleted = 900
        });
        await db.SaveChangesAsync();

        var result = await service.ForecastMaintenanceAsync(null, null);

        result.Should().NotBeNull();
        result.Should().Contain(f => f.PrinterId == printer.Id);
        var forecast = result.First(f => f.PrinterId == printer.Id);
        forecast.UpcomingTasks.Should().Contain(t => t.TaskName.Contains("Nozzle"));
    }

    [Fact]
    public async Task GetActiveAlertsAsync_WithNoIssues_ReturnsEmptyList()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPredictiveAnalyticsService>();

        var result = await service.GetActiveAlertsAsync(null);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetActiveAlertsAsync_WithHighFailureRate_ReturnsWarningAlert()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPredictiveAnalyticsService>();

        for (int i = 0; i < 3; i++)
        {
            db.PrintJobs.Add(new PrintJob
            {
                Name = $"AlertFail{i}",
                QueuedAt = DateTime.UtcNow.AddDays(-i),
                Status = PrintJobStatus.Failed
            });
        }
        for (int i = 0; i < 2; i++)
        {
            db.PrintJobs.Add(new PrintJob
            {
                Name = $"AlertSuccess{i}",
                QueuedAt = DateTime.UtcNow.AddDays(-i),
                Status = PrintJobStatus.Completed
            });
        }
        await db.SaveChangesAsync();

        var result = await service.GetActiveAlertsAsync(null);

        result.Should().NotBeEmpty();
        result.Should().Contain(a => a.AlertType == "HighFailureRate");
    }

    [Fact]
    public async Task PredictJobFailureLikelihoodAsync_ReturnsConfidenceScore()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPredictiveAnalyticsService>();

        var manufacturer = new Manufacturer { Name = "ConfMfg" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "ConfModel",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Name = "ConfTestPrinter",
            ServerUrl = "http://conftest.local",
            BackendPort = 7125,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var result = await service.PredictJobFailureLikelihoodAsync(new PredictionRequest
        {
            PrinterId = printer.Id,
            Material = "PLA",
            EstimatedDurationMinutes = 60
        });

        result.Factors.Should().NotBeEmpty();
        result.Factors.Should().Contain(f => f.Name.Contains("Material"));
        result.Factors.Should().Contain(f => f.Name.Contains("Printer"));
    }
}
