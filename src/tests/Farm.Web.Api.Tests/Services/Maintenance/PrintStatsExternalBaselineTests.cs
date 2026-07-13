using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Services.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

/// <summary>
/// Regression coverage for issue #711 round-5 BLOCKER: the external-backend baseline used for
/// per-toolhead wear attribution must be isolated from PrintFarmer-job inflation of
/// <see cref="PrinterStatistics.TotalPrintHours"/>. Prior to the fix the baseline was read from the
/// inflated total, so the external delta collapsed to zero on every cycle after the first and no
/// wear was ever attributed to toolheads.
/// </summary>
public class PrintStatsExternalBaselineTests
{
    [Fact]
    public async Task SyncPrinterStatisticsAsync_SecondCycle_AttributesExternalDeltaDespitePrintFarmerJobInflation()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Guid printerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();

        await using (var seed = new AppDbContext(options))
        {
            seed.Toolheads.Add(new Toolhead
            {
                Id = toolheadId,
                PrinterId = printerId,
                Name = "Toolhead 0",
                Index = 0,
                ToolheadType = ToolheadType.Physical,
                CumulativePrintHours = 0
            });
            await seed.SaveChangesAsync();
        }

        var printer = new Printer
        {
            Id = printerId,
            Name = "Printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = modelId
        };

        // External backend history grows from 100h to 110h across the two cycles.
        var externalHoursByCycle = new Queue<double>([100.0, 110.0]);
        var clientMock = new Mock<IBackendClient>();
        clientMock.As<ISupportsHistory>()
            .Setup(c => c.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalPrintTime = externalHoursByCycle.Dequeue() * 3600.0,
                    TotalJobs = 5
                }
            });

        var factoryMock = new Mock<IBackendClientFactory>();
        factoryMock.Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns(clientMock.Object);

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(factoryMock.Object)
            .BuildServiceProvider();

        // Nonzero PrintFarmer job history: a constant 50h every cycle that inflates TotalPrintHours
        // AFTER the external snapshot is taken.
        var pfJobs = new List<PrintJobStatistics>
        {
            new() { ActualDurationMs = (long)(50.0 * 3600 * 1000) }
        };
        var jobStatsMock = new Mock<IPrintJobStatisticsRepository>();
        jobStatsMock
            .Setup(r => r.GetByPrinterModelAsync(modelId, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pfJobs);

        var gateMock = new Mock<IOperatorFeatureGate>();
        gateMock.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);

        var settings = new PrintStatsSyncSettings { IncludePrintFarmerJobs = true, ApiTimeoutSeconds = 30 };

        var service = new PrintStatsSyncHostedService(
            provider,
            Mock.Of<ILogger<PrintStatsSyncHostedService>>(),
            Mock.Of<IOptionsMonitor<PrintStatsSyncSettings>>(),
            Mock.Of<IBackgroundServiceMonitor>());

        // Cycle 1: fresh statistics row. The full 100h external history seeds the printer-wide
        // counter but nothing is attributed to toolheads (no baseline yet).
        await using (var db1 = new AppDbContext(options))
        {
            await service.SyncPrinterStatisticsAsync(
                printer,
                settings,
                new EfPrinterStatisticsRepository(db1),
                new EfToolheadStatisticsRepository(db1),
                jobStatsMock.Object,
                gateMock.Object,
                provider,
                CancellationToken.None);
            await db1.SaveChangesAsync();
        }

        // Cycle 2: external history grew by 10h. Even though TotalPrintHours was inflated to 150h by
        // PrintFarmer jobs at the end of cycle 1, the external delta must be computed from the
        // dedicated ExternalPrintHours baseline (100h) and yield 10h attributed to the toolhead.
        await using (var db2 = new AppDbContext(options))
        {
            await service.SyncPrinterStatisticsAsync(
                printer,
                settings,
                new EfPrinterStatisticsRepository(db2),
                new EfToolheadStatisticsRepository(db2),
                jobStatsMock.Object,
                gateMock.Object,
                provider,
                CancellationToken.None);
            await db2.SaveChangesAsync();
        }

        await using (var verify = new AppDbContext(options))
        {
            Toolhead toolhead = await verify.Toolheads.FirstAsync(t => t.Id == toolheadId);
            toolhead.CumulativePrintHours.Should().BeApproximately(10.0, 0.0001,
                "the 10h external growth on the second cycle must be attributed despite PrintFarmer-job inflation");

            PrinterStatistics stats = await verify.PrinterStatisticsSet.FirstAsync(s => s.PrinterId == printerId);
            stats.ExternalPrintHours.Should().BeApproximately(110.0, 0.0001,
                "ExternalPrintHours must track only the external backend total");
            stats.ExternalJobsCompleted.Should().Be(5,
                "ExternalJobsCompleted must track only the external backend total");
            stats.TotalPrintHours.Should().BeApproximately(160.0, 0.0001,
                "TotalPrintHours is 110h external + 50h PrintFarmer jobs");
            stats.TotalJobsCompleted.Should().Be(6,
                "TotalJobsCompleted is 5 external jobs + 1 PrintFarmer job");
        }
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_PrusaLinkTwoCycles_DoesNotCompoundPrintFarmerTotals()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Printer printer = new()
        {
            Id = printerId,
            Name = "PrusaLink Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ModelId = modelId
        };
        List<PrintJobStatistics> pfJobs =
        [
            new() { ActualDurationMs = (long)(2.0 * 3600 * 1000) },
            new() { ActualDurationMs = (long)(1.0 * 3600 * 1000) }
        ];
        Mock<IPrintJobStatisticsRepository> jobStats = new();
        jobStats
            .Setup(r => r.GetByPrinterModelAsync(modelId, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pfJobs);
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        PrintStatsSyncHostedService service = CreateService(provider);
        PrintStatsSyncSettings settings = new()
        {
            IncludePrintFarmerJobs = true,
            ApiTimeoutSeconds = 30
        };

        for (int cycle = 0; cycle < 2; cycle++)
        {
            await using AppDbContext db = new(options);
            await service.SyncPrinterStatisticsAsync(
                printer,
                settings,
                new EfPrinterStatisticsRepository(db),
                new EfToolheadStatisticsRepository(db),
                jobStats.Object,
                Mock.Of<IOperatorFeatureGate>(),
                provider,
                CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using AppDbContext verify = new(options);
        PrinterStatistics stats = await verify.PrinterStatisticsSet.SingleAsync();
        stats.ExternalPrintHours.Should().Be(0);
        stats.ExternalJobsCompleted.Should().Be(0);
        stats.TotalPrintHours.Should().BeApproximately(3.0, 0.0001,
            "absolute PrintFarmer history must be re-added to a clean baseline each cycle");
        stats.TotalJobsCompleted.Should().Be(2);
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_ExternalFailureTwoCycles_UsesLastKnownBaselines()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        await using (AppDbContext seed = new(options))
        {
            seed.PrinterStatisticsSet.Add(new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                ExternalPrintHours = 100,
                ExternalJobsCompleted = 5,
                TotalPrintHours = 150,
                TotalJobsCompleted = 6
            });
            await seed.SaveChangesAsync();
        }

        Printer printer = new()
        {
            Id = printerId,
            Name = "Moonraker Printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = modelId
        };
        Mock<IBackendClient> client = new();
        client.As<ISupportsHistory>()
            .Setup(c => c.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HistoryTotals?)null);
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(factory.Object)
            .BuildServiceProvider();
        Mock<IPrintJobStatisticsRepository> jobStats = new();
        jobStats
            .Setup(r => r.GetByPrinterModelAsync(modelId, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PrintJobStatistics { ActualDurationMs = (long)(50.0 * 3600 * 1000) }
            ]);
        PrintStatsSyncHostedService service = CreateService(provider);
        PrintStatsSyncSettings settings = new()
        {
            IncludePrintFarmerJobs = true,
            ApiTimeoutSeconds = 30
        };

        for (int cycle = 0; cycle < 2; cycle++)
        {
            await using AppDbContext db = new(options);
            await service.SyncPrinterStatisticsAsync(
                printer,
                settings,
                new EfPrinterStatisticsRepository(db),
                new EfToolheadStatisticsRepository(db),
                jobStats.Object,
                Mock.Of<IOperatorFeatureGate>(),
                provider,
                CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using AppDbContext verify = new(options);
        PrinterStatistics stats = await verify.PrinterStatisticsSet.SingleAsync();
        stats.ExternalPrintHours.Should().Be(100);
        stats.ExternalJobsCompleted.Should().Be(5);
        stats.TotalPrintHours.Should().BeApproximately(150.0, 0.0001);
        stats.TotalJobsCompleted.Should().Be(6);
    }

    private static PrintStatsSyncHostedService CreateService(IServiceProvider provider)
    {
        return new PrintStatsSyncHostedService(
            provider,
            Mock.Of<ILogger<PrintStatsSyncHostedService>>(),
            Mock.Of<IOptionsMonitor<PrintStatsSyncSettings>>(),
            Mock.Of<IBackgroundServiceMonitor>());
    }
}
