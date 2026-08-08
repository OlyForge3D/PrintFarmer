using System;
using System.Collections.Generic;
using System.Linq;
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

        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"TestMfg-{manufacturerId:N}" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = $"TestModel-{modelId:N}", ManufacturerId = manufacturerId });
        db.SaveChanges();

        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = $"Test Printer {printerId:N}",
            ServerUrl = $"http://192.168.1.100/{printerId:N}",
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

        costServiceMock.Verify(
            c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task AggregateCompletedJobs_ReadingsAtWindowBoundaries_AreIncludedInclusive()
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

            // Readings sit exactly at jobStart and exactly at jobEnd — the query filter is
            // inclusive on both ends (>= start && <= end), so both must be counted.
            // Sum = 100 + 300 = 400W; interval = 30s -> kWh = 400 * 30 / 3_600_000 = 0.0033(3)
            db.PowerReadings.AddRange(
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitor.Id, WattsNow = 100m, RecordedAt = jobStart },
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitor.Id, WattsNow = 300m, RecordedAt = jobEnd });
            db.SaveChanges();
        }

        Mock<IJobCostCalculationService> costServiceMock = new();
        costServiceMock
            .Setup(c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PrintJob? updatedJob = await RunAggregationCycleAsync(CreateContext, costServiceMock, jobId);

        Assert.NotNull(updatedJob);
        Assert.Equal(Math.Round(400m * 30m / 3_600_000m, 4), updatedJob!.KwhUsed);
    }

    [Fact]
    public async Task AggregateCompletedJobs_MultipleMonitorsOnSamePrinter_SumsAcrossAllMonitors()
    {
        DateTime jobStart = DateTime.UtcNow.AddHours(-2);
        DateTime jobEnd = DateTime.UtcNow.AddHours(-1);
        Guid jobId;

        using (AppDbContext db = CreateContext())
        {
            (Guid printerId, _, _, Guid seededJobId) = SeedPrinterAndJob(db, jobStart, jobEnd);
            jobId = seededJobId;

            // Two monitors on the same printer (e.g. printer + auxiliary heater plug) —
            // monitorIds.Contains(...) must aggregate readings from both.
            var monitorA = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.200",
                IsEnabled = true,
            };
            var monitorB = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.201",
                IsEnabled = true,
            };
            db.PowerMonitors.AddRange(monitorA, monitorB);
            db.SaveChanges();

            // monitorA: 100W, monitorB: 250W -> sum = 350W; interval = 30s
            // kWh = 350 * 30 / 3_600_000 = 0.0029(1..)
            db.PowerReadings.AddRange(
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitorA.Id, WattsNow = 100m, RecordedAt = jobStart.AddMinutes(10) },
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitorB.Id, WattsNow = 250m, RecordedAt = jobStart.AddMinutes(10) });
            db.SaveChanges();
        }

        Mock<IJobCostCalculationService> costServiceMock = new();
        costServiceMock
            .Setup(c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PrintJob? updatedJob = await RunAggregationCycleAsync(CreateContext, costServiceMock, jobId);

        Assert.NotNull(updatedJob);
        Assert.Equal(Math.Round(350m * 30m / 3_600_000m, 4), updatedJob!.KwhUsed);
    }

    [Fact]
    public async Task AggregateCompletedJobs_MultiplePrinters_ExcludesOtherPrintersReadings()
    {
        DateTime jobStart = DateTime.UtcNow.AddHours(-2);
        DateTime jobEnd = DateTime.UtcNow.AddHours(-1);
        Guid jobAId;
        Guid jobBId;

        using (AppDbContext db = CreateContext())
        {
            (Guid printerAId, _, _, Guid seededJobAId) = SeedPrinterAndJob(db, jobStart, jobEnd);
            jobAId = seededJobAId;
            (Guid printerBId, _, _, Guid seededJobBId) = SeedPrinterAndJob(db, jobStart, jobEnd);
            jobBId = seededJobBId;

            var monitorA = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerAId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.200",
                IsEnabled = true,
            };
            var monitorB = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerBId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.201",
                IsEnabled = true,
            };
            db.PowerMonitors.AddRange(monitorA, monitorB);
            db.SaveChanges();

            // Overlapping time windows, different printers/monitors — readings must not
            // cross-contaminate: job A should only see monitorA's 100W, not monitorB's 900W.
            db.PowerReadings.AddRange(
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitorA.Id, WattsNow = 100m, RecordedAt = jobStart.AddMinutes(10) },
                new Farm.Infrastructure.Domain.PowerReading { PowerMonitorId = monitorB.Id, WattsNow = 900m, RecordedAt = jobStart.AddMinutes(10) });
            db.SaveChanges();
        }

        Mock<IJobCostCalculationService> costServiceMock = new();
        costServiceMock
            .Setup(c => c.CalculateAndStoreCostsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Run one aggregation cycle covering both jobs, then assert each job's KwhUsed only
        // reflects its own printer's monitor.
        ServiceCollection svcCollection = new();
        svcCollection.AddLogging();
        svcCollection.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("PowerMonitor:PollIntervalSeconds", "30"),
            })
            .Build());
        svcCollection.AddScoped(_ => CreateContext());
        svcCollection.AddScoped<IJobCostCalculationService>(_ => costServiceMock.Object);
        svcCollection.AddScoped<ISmartPlugProvider, NullSmartPlugProvider>();

        using ServiceProvider provider = svcCollection.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        PowerMonitorPollingService svc = new(
            scopeFactory,
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<PowerMonitorPollingService>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await svc.StopAsync(CancellationToken.None);

        using AppDbContext assertContext = CreateContext();
        PrintJob? jobA = assertContext.PrintJobs.FirstOrDefault(j => j.Id == jobAId);
        PrintJob? jobB = assertContext.PrintJobs.FirstOrDefault(j => j.Id == jobBId);

        Assert.NotNull(jobA);
        Assert.NotNull(jobB);
        Assert.Equal(Math.Round(100m * 30m / 3_600_000m, 4), jobA!.KwhUsed);
        Assert.Equal(Math.Round(900m * 30m / 3_600_000m, 4), jobB!.KwhUsed);
    }

    /// <summary>
    /// Confirms the rewritten aggregation query is translated to server-side SQL
    /// (a single GROUP BY producing COUNT/SUM) rather than silently falling back to
    /// client evaluation, which would reintroduce the original in-memory materialization
    /// this change eliminates.
    /// </summary>
    [Fact]
    public void AggregationQuery_TranslatesToServerSideGroupByAggregate()
    {
        using AppDbContext db = CreateContext();

        DateTime start = DateTime.UtcNow.AddHours(-2);
        DateTime end = DateTime.UtcNow.AddHours(-1);
        var monitorIds = new List<int> { 1, 2, 3 };

        string sql = db.PowerReadings
            .Where(r =>
                monitorIds.Contains(r.PowerMonitorId) &&
                r.RecordedAt >= start &&
                r.RecordedAt <= end)
            .GroupBy(r => 1)
            .Select(g => new { Count = g.Count(), WattsSum = g.Sum(r => r.WattsNow) })
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", sql, StringComparison.OrdinalIgnoreCase);
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
