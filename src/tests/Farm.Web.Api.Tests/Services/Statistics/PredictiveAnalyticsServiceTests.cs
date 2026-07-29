using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task GetActiveAlertsAsync_WithMultiplePrinters_BatchesTrendQueries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var interceptor = new PrintJobQueryCountingInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var manufacturer = new Manufacturer { Name = "TrendMfg" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "TrendModel",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var decliningPrinter = CreatePrinter("DecliningPrinter", "http://declining.local", manufacturer.Id, model.Id);
        var stablePrinter = CreatePrinter("StablePrinter", "http://stable.local", manufacturer.Id, model.Id);
        var noHistoryPrinter = CreatePrinter("NoHistoryPrinter", "http://no-history.local", manufacturer.Id, model.Id);
        db.Printers.AddRange(decliningPrinter, stablePrinter, noHistoryPrinter);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        for (int i = 0; i < 4; i++)
        {
            db.PrintJobs.Add(CreateJob(decliningPrinter.Id, PrintJobStatus.Completed, now.AddDays(-10)));
            db.PrintJobs.Add(CreateJob(stablePrinter.Id, PrintJobStatus.Completed, now.AddDays(-10)));
            db.PrintJobs.Add(CreateJob(stablePrinter.Id, PrintJobStatus.Completed, now.AddDays(-2)));
        }

        db.PrintJobs.Add(CreateJob(decliningPrinter.Id, PrintJobStatus.Completed, now.AddDays(-2)));
        for (int i = 0; i < 3; i++)
        {
            db.PrintJobs.Add(CreateJob(decliningPrinter.Id, PrintJobStatus.Failed, now.AddDays(-2)));
        }

        await db.SaveChangesAsync();

        var service = new PredictiveAnalyticsService(db);
        interceptor.Reset();

        var result = await service.GetActiveAlertsAsync();

        result.Where(alert => alert.AlertType == "DecliningPerformance")
            .Should()
            .ContainSingle(alert => alert.Message.Contains(decliningPrinter.Name, StringComparison.Ordinal));
        interceptor.PrintJobQueryCount.Should().BeLessThanOrEqualTo(3);
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

    private static Printer CreatePrinter(
        string name,
        string serverUrl,
        Guid manufacturerId,
        Guid modelId) =>
        new()
        {
            Name = name,
            ServerUrl = serverUrl,
            BackendPort = 7125,
            ModelId = modelId,
            ManufacturerId = manufacturerId,
            Backend = (int)PrinterBackend.Moonraker
        };

    private static PrintJob CreateJob(Guid printerId, PrintJobStatus status, DateTime queuedAt) =>
        new()
        {
            Name = $"{status}-{Guid.NewGuid()}",
            AssignedPrinterId = printerId,
            QueuedAt = queuedAt,
            Status = status
        };

    private sealed class PrintJobQueryCountingInterceptor : DbCommandInterceptor
    {
        private int _printJobQueryCount;

        public int PrintJobQueryCount => Volatile.Read(ref _printJobQueryCount);

        public void Reset() => Interlocked.Exchange(ref _printJobQueryCount, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"PrintJobs\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _printJobQueryCount);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
