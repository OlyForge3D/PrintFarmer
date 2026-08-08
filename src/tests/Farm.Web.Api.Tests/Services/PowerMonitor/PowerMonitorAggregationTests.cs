using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cost;
using Farm.Web.Api.Services.PowerMonitor;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PowerMonitor;

/// <summary>
/// Correctness tests for <see cref="PowerMonitorPollingService"/>'s server-side aggregation
/// (COUNT + SUM pushed into SQL instead of materializing every <see cref="PowerReading"/>).
/// Verifies the computed <see cref="PrintJob.KwhUsed"/> is unchanged from the equivalent
/// in-memory sum, across multiple-reading, single-reading, and no-reading windows.
/// </summary>
public class PowerMonitorAggregationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PowerMonitorAggregationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateContext() => new(_options);

    private static (Guid printerId, Guid manufacturerId, Guid modelId, Guid jobId) SeedPrinterAndJob(
        AppDbContext db, DateTime jobStart, DateTime jobEnd)
    {
        Guid printerId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestMfg" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "TestModel", ManufacturerId = manufacturerId });
        db.SaveChanges();

        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Test Printer",
            ServerUrl = "http://192.168.1.100:7125",
            BackendPort = 7125,
            Backend = 1,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
        });
        db.SaveChanges();

        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Name = "test.gcode",
            Status = PrintJobStatus.Completed,
            AssignedPrinterId = printerId,
            ActualStartTime = jobStart,
            ActualEndTime = jobEnd,
            KwhUsed = null,
        });
        db.SaveChanges();

        return (printerId, manufacturerId, modelId, jobId);
    }

    private static async Task<PrintJob?> RunAggregationCycleAsync(
        Func<AppDbContext> contextFactory,
        Mock<IJobCostCalculationService> costServiceMock,
        Guid jobId)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("PowerMonitor:PollIntervalSeconds", "30"),
            })
            .Build());
        services.AddScoped(_ => contextFactory());
        services.AddScoped<IJobCostCalculationService>(_ => costServiceMock.Object);
        services.AddScoped<ISmartPlugProvider, NullSmartPlugProvider>();

        using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        PowerMonitorPollingService svc = new(
            scopeFactory,
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<PowerMonitorPollingService>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await svc.StopAsync(CancellationToken.None);

        using AppDbContext assertContext = contextFactory();
        return assertContext.PrintJobs.FirstOrDefault(j => j.Id == jobId);
    }

    [Fact]
    public async Task AggregateCompletedJobs_MultipleReadings_ComputesExactKwh()
    {
        DateTime jobStart = DateTime.UtcNow.AddHours(-2);
        DateTime jobEnd = DateTime.UtcNow.AddHours(-1);
        Guid jobId;

        using (AppDbContext db = CreateContext())
        {
            (Guid printerId, _, _, Guid seededJobId) = SeedPrinterAndJob(db, jobStart, jobEnd);
            jobId = seededJobId;

            var monitor = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.200",
                IsEnabled = true,
            };
            db.PowerMonitors.Add(monitor);
            db.SaveChanges();

            // Sum = 100 + 200 + 300 = 600W; interval = 30s
            // kWh = 600 * 30 / 3_600_000 = 0.0050
            db.PowerReadings.AddRange(
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitor.Id, WattsNow = 100m, RecordedAt = jobStart.AddMinutes(10) },
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitor.Id, WattsNow = 200m, RecordedAt = jobStart.AddMinutes(20) },
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitor.Id, WattsNow = 300m, RecordedAt = jobStart.AddMinutes(30) });
            db.SaveChanges();
        }

        Mock<IJobCostCalculationService> costServiceMock = new();
        costServiceMock
            .Setup(c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PrintJob? updatedJob = await RunAggregationCycleAsync(CreateContext, costServiceMock, jobId);

        Assert.NotNull(updatedJob);
        Assert.Equal(0.0050m, updatedJob!.KwhUsed);

        costServiceMock.Verify(
            c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AggregateCompletedJobs_SingleReading_ComputesExactKwh()
    {
        DateTime jobStart = DateTime.UtcNow.AddHours(-2);
        DateTime jobEnd = DateTime.UtcNow.AddHours(-1);
        Guid jobId;

        using (AppDbContext db = CreateContext())
        {
            (Guid printerId, _, _, Guid seededJobId) = SeedPrinterAndJob(db, jobStart, jobEnd);
            jobId = seededJobId;

            var monitor = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.200",
                IsEnabled = true,
            };
            db.PowerMonitors.Add(monitor);
            db.SaveChanges();

            // Single reading: 120W over 30s interval -> kWh = 120 * 30 / 3_600_000 = 0.0010
            db.PowerReadings.Add(new Farm.Infrastructure.Domain.PowerReading
            {
                PowerMonitorId = monitor.Id,
                WattsNow = 120m,
                RecordedAt = jobStart.AddMinutes(15),
            });
            db.SaveChanges();
        }

        Mock<IJobCostCalculationService> costServiceMock = new();
        costServiceMock
            .Setup(c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PrintJob? updatedJob = await RunAggregationCycleAsync(CreateContext, costServiceMock, jobId);

        Assert.NotNull(updatedJob);
        Assert.Equal(0.0010m, updatedJob!.KwhUsed);
    }

    [Fact]
    public async Task AggregateCompletedJobs_NoReadingsInWindow_LeavesKwhUsedNull()
    {
        DateTime jobStart = DateTime.UtcNow.AddHours(-2);
        DateTime jobEnd = DateTime.UtcNow.AddHours(-1);
        Guid jobId;

        using (AppDbContext db = CreateContext())
        {
            (Guid printerId, _, _, Guid seededJobId) = SeedPrinterAndJob(db, jobStart, jobEnd);
            jobId = seededJobId;

            var monitor = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.200",
                IsEnabled = true,
            };
            db.PowerMonitors.Add(monitor);
            db.SaveChanges();

            // Reading exists but falls outside the job window — should not be aggregated.
            db.PowerReadings.Add(new Farm.Infrastructure.Domain.PowerReading
            {
                PowerMonitorId = monitor.Id,
                WattsNow = 500m,
                RecordedAt = jobEnd.AddHours(1),
            });
            db.SaveChanges();
        }

        Mock<IJobCostCalculationService> costServiceMock = new();
        costServiceMock
            .Setup(c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PrintJob? updatedJob = await RunAggregationCycleAsync(CreateContext, costServiceMock, jobId);

        Assert.NotNull(updatedJob);
        Assert.Null(updatedJob!.KwhUsed);

        costServiceMock.Verify(
            c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class NullSmartPlugProvider : ISmartPlugProvider
    {
        public string ProviderType => "Kasa";

        public Task<Farm.Web.Api.Services.SmartPlug.PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
            => Task.FromResult<Farm.Web.Api.Services.SmartPlug.PowerReading?>(null);

        public Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
            => Task.FromResult(true);
    }
}
