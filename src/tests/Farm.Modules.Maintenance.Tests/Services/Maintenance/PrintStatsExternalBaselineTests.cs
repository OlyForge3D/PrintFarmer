using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using Farm.Modules.Maintenance.Services.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Modules.Maintenance.Tests.Services.Maintenance;

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
        gateMock.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

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

            // Issue #711 Finding H1: per-toolhead wear is only attributed when the printer advertises
            // SupportsPerToolAttribution AND real per-tool telemetry is available. This Moonraker
            // printer leaves the capability flag at its default (false), so the external delta must
            // advance the printer-wide ExternalPrintHours / TotalPrintHours baselines below WITHOUT
            // being fabricated onto the toolhead (the old equal-split of idle-head wear is removed).
            // The capability + telemetry attribution path is covered by PrintStatsToolheadAttributionTests.
            toolhead.CumulativePrintHours.Should().BeApproximately(0.0, 0.0001,
                "without SupportsPerToolAttribution the external delta must not be attributed to toolheads");

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
                TotalJobsCompleted = 6,
                // Already-synced printer: the external baseline was captured on a prior successful
                // sync (issue #711, round-7 Finding 1), so a subsequent failure re-adds PrintFarmer
                // history to the last-known external baseline (100h + 50h = 150h) rather than leaving
                // the total stale or doubling it.
                ExternalBaselineInitializedUtc = DateTime.UtcNow
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

    [Fact]
    public async Task SyncPrinterStatisticsAsync_ExistingInflatedPrusaLinkRow_DoesNotDoubleTotals()
    {
        // Issue #711 round-7 Finding 1: a PrusaLink-shaped row that pre-dates the fix carries a
        // TotalPrintHours/TotalJobsCompleted value already inflated by the prior PF-absolute
        // compounding bug. After migration ExternalPrintHours defaults to 0 and the external
        // baseline is uninitialized (ExternalBaselineInitializedUtc == null). The first sync must
        // snapshot an AUTHORITATIVE ZERO external baseline (not the polluted total) and re-derive
        // TotalPrintHours from the clean PrintFarmer aggregate, and subsequent cycles must be
        // idempotent (no doubling).
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
                // Post-migration state: external baseline defaulted to 0 and NOT yet initialized.
                ExternalPrintHours = 0,
                ExternalJobsCompleted = 0,
                ExternalBaselineInitializedUtc = null,
                // Pre-existing inflation from the compounding bug.
                TotalPrintHours = 250,
                TotalJobsCompleted = 99
            });
            await seed.SaveChangesAsync();
        }

        Printer printer = new()
        {
            Id = printerId,
            Name = "PrusaLink Printer",
            Backend = (int)PrinterBackend.PrusaLink,
            ModelId = modelId
        };
        // Clean PrintFarmer job history: 3h across 2 jobs.
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

        double afterCycle1Hours = 0;
        int afterCycle1Jobs = 0;
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

            if (cycle == 0)
            {
                PrinterStatistics afterFirst = await db.PrinterStatisticsSet.SingleAsync();
                afterCycle1Hours = afterFirst.TotalPrintHours;
                afterCycle1Jobs = afterFirst.TotalJobsCompleted;
            }
        }

        await using AppDbContext verify = new(options);
        PrinterStatistics stats = await verify.PrinterStatisticsSet.SingleAsync();
        stats.ExternalPrintHours.Should().Be(0,
            "an unsupported external backend snapshots an authoritative zero baseline");
        stats.ExternalJobsCompleted.Should().Be(0);
        stats.TotalPrintHours.Should().BeApproximately(3.0, 0.0001,
            "the inflated 250h total must be corrected to the clean PrintFarmer aggregate");
        stats.TotalJobsCompleted.Should().Be(2,
            "the inflated 99 job count must be corrected to the clean PrintFarmer aggregate");
        stats.TotalPrintHours.Should().BeApproximately(afterCycle1Hours, 0.0001,
            "the second cycle must be idempotent and NOT double the total");
        stats.TotalJobsCompleted.Should().Be(afterCycle1Jobs,
            "the second cycle must be idempotent and NOT double the job count");
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_RestartWindow_UsesPersistedAttributionBoundaryForCoverage()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid t0Id = Guid.NewGuid();
        Guid t1Id = Guid.NewGuid();
        DateTime lastDrainUtc = DateTime.UtcNow.AddHours(-4);

        await using (AppDbContext seed = new(options))
        {
            seed.PrinterStatisticsSet.Add(new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                TotalPrintHours = 100,
                ExternalPrintHours = 100,
                ExternalJobsCompleted = 10,
                TotalJobsCompleted = 10,
                ExternalBaselineInitializedUtc = lastDrainUtc.AddHours(-1),
                LastExternalHoursAttributionUtc = lastDrainUtc
            });
            seed.Toolheads.AddRange(
                new Toolhead
                {
                    Id = t0Id,
                    PrinterId = printerId,
                    Name = "T0",
                    Index = 0,
                    ToolheadType = ToolheadType.Physical
                },
                new Toolhead
                {
                    Id = t1Id,
                    PrinterId = printerId,
                    Name = "T1",
                    Index = 1,
                    ToolheadType = ToolheadType.Physical
                });
            await seed.SaveChangesAsync();
        }

        var printer = new Printer
        {
            Id = printerId,
            Name = "Restarted Moonraker printer",
            Backend = (int)PrinterBackend.Moonraker,
            BackendPort = 7125,
            ModelId = modelId,
            ServerUrl = "http://moonraker.local",
            SupportsPerToolAttribution = true
        };
        Mock<IBackendClient> client = new();
        client.As<ISupportsHistory>()
            .Setup(history => history.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalPrintTime = 104 * 3600,
                    TotalJobs = 11
                }
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(candidate => candidate.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);
        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(gate => gate.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        featureGate.Setup(gate => gate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var clock = new ManualTimeProvider();
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10), clock);
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(factory.Object)
            .AddSingleton<IToolheadActivityAccumulator>(accumulator)
            .BuildServiceProvider();
        PrintStatsSyncHostedService service = CreateService(provider);

        await using (AppDbContext db = new(options))
        {
            var statsRepo = new EfPrinterStatisticsRepository(db);
            ToolheadActivitySnapshot? snapshot = await service.SyncPrinterStatisticsAsync(
                printer,
                new PrintStatsSyncSettings
                {
                    IncludePrintFarmerJobs = false,
                    ApiTimeoutSeconds = 30
                },
                statsRepo,
                new EfToolheadStatisticsRepository(db),
                Mock.Of<IPrintJobStatisticsRepository>(),
                featureGate.Object,
                provider,
                CancellationToken.None);
            await PrintStatsSyncHostedService.CommitAndAcknowledgeAsync(
                statsRepo,
                accumulator,
                snapshot,
                CancellationToken.None);
        }

        await using AppDbContext verify = new(options);
        Toolhead t0 = await verify.Toolheads.SingleAsync(toolhead => toolhead.Id == t0Id);
        Toolhead t1 = await verify.Toolheads.SingleAsync(toolhead => toolhead.Id == t1Id);
        PrinterStatistics stats = await verify.PrinterStatisticsSet.SingleAsync(s => s.PrinterId == printerId);

        t0.CumulativePrintHours.Should().Be(0);
        t1.CumulativePrintHours.Should().BeApproximately(30d / 3600d, 0.001,
            "only the 30 observed post-restart seconds should be credited across the full persisted 4h window");
        (t0.CumulativePrintHours + t1.CumulativePrintHours).Should().BeLessThan(4,
            "the unobserved restart gap remains unattributed");
        stats.ExternalPrintHours.Should().BeApproximately(104, 0.0001);
        stats.LastExternalHoursAttributionUtc.Should().NotBeNull();
        stats.LastExternalHoursAttributionUtc!.Value.Should().BeAfter(lastDrainUtc);
        accumulator.PeekActiveSeconds(printerId).WindowSeconds.Should().Be(0);
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_ExternalHistoryReset_DiscardsDeltaAndAdvancesBoundaryWithoutCrediting()
    {
        // Issue #711, round-19 M19-1: a Moonraker history.reset_totals call (or any backend total
        // that decreases) must not silently replace the baseline while leaving the attribution
        // boundary and accumulator untouched -- otherwise the NEXT cycle's real increase would
        // attribute hours using samples that span across the reset. This proves the reset itself
        // credits nothing AND drains the pending accumulator snapshot, so no residual samples leak
        // into the next epoch.
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        DateTime priorBoundaryUtc = DateTime.UtcNow.AddMinutes(-1);

        await using (AppDbContext seed = new(options))
        {
            seed.PrinterStatisticsSet.Add(new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                TotalPrintHours = 100,
                ExternalPrintHours = 100,
                ExternalJobsCompleted = 10,
                TotalJobsCompleted = 10,
                ExternalBaselineInitializedUtc = DateTime.UtcNow.AddHours(-2),
                LastExternalHoursAttributionUtc = priorBoundaryUtc
            });
            seed.Toolheads.Add(new Toolhead
            {
                Id = toolheadId,
                PrinterId = printerId,
                Name = "T0",
                Index = 0,
                ToolheadType = ToolheadType.Physical
            });
            await seed.SaveChangesAsync();
        }

        Printer printer = new()
        {
            Id = printerId,
            Name = "Reset Moonraker printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = modelId,
            ServerUrl = "http://moonraker.local",
            SupportsPerToolAttribution = true
        };
        Mock<IBackendClient> client = new();
        client.As<ISupportsHistory>()
            .Setup(history => history.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals
            {
                // The backend total collapsed from 100h to 5h -- a history.reset_totals discontinuity.
                JobTotals = new JobTotals { TotalPrintTime = 5 * 3600, TotalJobs = 1 }
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(candidate => candidate.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);
        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(gate => gate.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        featureGate.Setup(gate => gate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var clock = new ManualTimeProvider();
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10), clock);
        // Telemetry recorded before the reset belongs to the (soon-to-be-discarded) epoch. Without
        // the fix this would leak into whichever cycle finally advances the boundary.
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(45));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(factory.Object)
            .AddSingleton<IToolheadActivityAccumulator>(accumulator)
            .BuildServiceProvider();
        PrintStatsSyncHostedService service = CreateService(provider);

        await using (AppDbContext db = new(options))
        {
            var statsRepo = new EfPrinterStatisticsRepository(db);
            ToolheadActivitySnapshot? snapshot = await service.SyncPrinterStatisticsAsync(
                printer,
                new PrintStatsSyncSettings { IncludePrintFarmerJobs = false, ApiTimeoutSeconds = 30 },
                statsRepo,
                new EfToolheadStatisticsRepository(db),
                Mock.Of<IPrintJobStatisticsRepository>(),
                featureGate.Object,
                provider,
                CancellationToken.None);
            await PrintStatsSyncHostedService.CommitAndAcknowledgeAsync(
                statsRepo,
                accumulator,
                snapshot,
                CancellationToken.None);
        }

        await using AppDbContext verify = new(options);
        Toolhead toolhead = await verify.Toolheads.SingleAsync(t => t.Id == toolheadId);
        PrinterStatistics stats = await verify.PrinterStatisticsSet.SingleAsync(s => s.PrinterId == printerId);

        stats.ExternalPrintHours.Should().Be(5,
            "the baseline must always track the latest fresh read, even after a decrease");
        stats.LastExternalHoursAttributionUtc.Should().NotBeNull();
        stats.LastExternalHoursAttributionUtc!.Value.Should().BeAfter(priorBoundaryUtc,
            "the attribution boundary must advance past the reset instead of staying stale");
        toolhead.CumulativePrintHours.Should().Be(0,
            "a counter decrease must never be attributed as wear");
        accumulator.PeekActiveSeconds(printerId).WindowSeconds.Should().Be(0,
            "the pending accumulator snapshot must be acknowledged/drained across the reset, even " +
            "though zero hours were credited, so stale samples do not leak into the next epoch");
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_TransientDipThenRecovery_TreatsReboundAsDiscontinuityWithoutRecrediting()
    {
        // Issue #711, round-19 M19-1: a transient dip (e.g. a bad read of 5h after 100h) followed
        // immediately by a "recovery" back toward the true total must not re-credit the historical
        // gap as if it were real wear observed in the few milliseconds between the two sync ticks.
        // Both the dip AND the implausible rebound are discontinuities; only a subsequent PLAUSIBLE
        // increase (relative to real elapsed time) would ever be credited.
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();

        await using (AppDbContext seed = new(options))
        {
            seed.PrinterStatisticsSet.Add(new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                TotalPrintHours = 100,
                ExternalPrintHours = 100,
                ExternalJobsCompleted = 10,
                TotalJobsCompleted = 10,
                ExternalBaselineInitializedUtc = DateTime.UtcNow.AddHours(-2),
                LastExternalHoursAttributionUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            seed.Toolheads.Add(new Toolhead
            {
                Id = toolheadId,
                PrinterId = printerId,
                Name = "T0",
                Index = 0,
                ToolheadType = ToolheadType.Physical
            });
            await seed.SaveChangesAsync();
        }

        Printer printer = new()
        {
            Id = printerId,
            Name = "Dip-recovery Moonraker printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = modelId,
            ServerUrl = "http://moonraker.local",
            SupportsPerToolAttribution = true
        };
        // First cycle reads the dip (5h); second cycle reads a "recovery" (8h) -- a 3h jump that
        // cannot possibly be real wear given the near-instant real elapsed time between the two
        // test cycles.
        Queue<double> externalHoursByCycle = new([5.0, 8.0]);
        Mock<IBackendClient> client = new();
        client.As<ISupportsHistory>()
            .Setup(history => history.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalPrintTime = externalHoursByCycle.Dequeue() * 3600,
                    TotalJobs = 1
                }
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(candidate => candidate.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);
        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(gate => gate.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        featureGate.Setup(gate => gate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var clock = new ManualTimeProvider();
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10), clock);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(factory.Object)
            .AddSingleton<IToolheadActivityAccumulator>(accumulator)
            .BuildServiceProvider();
        PrintStatsSyncHostedService service = CreateService(provider);
        PrintStatsSyncSettings settings = new() { IncludePrintFarmerJobs = false, ApiTimeoutSeconds = 30 };

        // Cycle A: the dip (100h -> 5h).
        await using (AppDbContext db = new(options))
        {
            var statsRepo = new EfPrinterStatisticsRepository(db);
            ToolheadActivitySnapshot? snapshot = await service.SyncPrinterStatisticsAsync(
                printer,
                settings,
                statsRepo,
                new EfToolheadStatisticsRepository(db),
                Mock.Of<IPrintJobStatisticsRepository>(),
                featureGate.Object,
                provider,
                CancellationToken.None);
            await PrintStatsSyncHostedService.CommitAndAcknowledgeAsync(
                statsRepo, accumulator, snapshot, CancellationToken.None);
        }

        // More telemetry accrues between the two ticks; if the fix were absent this would be
        // exactly the kind of "real" per-tool coverage that would let the recovery's 3h delta be
        // fully attributed.
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        // Cycle B: the "recovery" (5h -> 8h).
        await using (AppDbContext db = new(options))
        {
            var statsRepo = new EfPrinterStatisticsRepository(db);
            ToolheadActivitySnapshot? snapshot = await service.SyncPrinterStatisticsAsync(
                printer,
                settings,
                statsRepo,
                new EfToolheadStatisticsRepository(db),
                Mock.Of<IPrintJobStatisticsRepository>(),
                featureGate.Object,
                provider,
                CancellationToken.None);
            await PrintStatsSyncHostedService.CommitAndAcknowledgeAsync(
                statsRepo, accumulator, snapshot, CancellationToken.None);
        }

        await using AppDbContext verify = new(options);
        Toolhead toolhead = await verify.Toolheads.SingleAsync(t => t.Id == toolheadId);
        PrinterStatistics stats = await verify.PrinterStatisticsSet.SingleAsync(s => s.PrinterId == printerId);

        stats.ExternalPrintHours.Should().Be(8,
            "the baseline must track the latest fresh read after both the dip and the recovery");
        toolhead.CumulativePrintHours.Should().Be(0,
            "neither the dip nor the implausible rebound may be attributed as wear");
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_NormalIncrease_AttributesUnaffectedByDiscontinuityGuard()
    {
        // Issue #711, round-19 M19-1: the discontinuity guard must not interfere with a genuine,
        // physically-plausible increase -- it only rejects decreases and rebounds that could not
        // possibly represent real elapsed wear.
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        DateTime priorBoundaryUtc = DateTime.UtcNow.AddHours(-1);

        await using (AppDbContext seed = new(options))
        {
            seed.PrinterStatisticsSet.Add(new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                TotalPrintHours = 100,
                ExternalPrintHours = 100,
                ExternalJobsCompleted = 10,
                TotalJobsCompleted = 10,
                ExternalBaselineInitializedUtc = DateTime.UtcNow.AddHours(-2),
                LastExternalHoursAttributionUtc = priorBoundaryUtc
            });
            seed.Toolheads.Add(new Toolhead
            {
                Id = toolheadId,
                PrinterId = printerId,
                Name = "T0",
                Index = 0,
                ToolheadType = ToolheadType.Physical
            });
            await seed.SaveChangesAsync();
        }

        Printer printer = new()
        {
            Id = printerId,
            Name = "Steady Moonraker printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = modelId,
            ServerUrl = "http://moonraker.local",
            SupportsPerToolAttribution = true
        };
        Mock<IBackendClient> client = new();
        client.As<ISupportsHistory>()
            .Setup(history => history.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals
            {
                // A plausible 0.5h increase over the persisted 1h-ago boundary.
                JobTotals = new JobTotals { TotalPrintTime = 100.5 * 3600, TotalJobs = 11 }
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(candidate => candidate.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);
        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(gate => gate.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        featureGate.Setup(gate => gate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var clock = new ManualTimeProvider();
        // Max segment must cover the full 1h sampling interval below (the default 10-minute test cap
        // would otherwise treat this single long segment as a stale/dropped-telemetry gap and reject
        // it from the numerator entirely).
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromHours(2), clock);
        // Observed active-tool telemetry spans the full persisted 1h window, so coverage is ~100%
        // and the entire plausible delta is attributed.
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromHours(1));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(factory.Object)
            .AddSingleton<IToolheadActivityAccumulator>(accumulator)
            .BuildServiceProvider();
        PrintStatsSyncHostedService service = CreateService(provider);

        await using (AppDbContext db = new(options))
        {
            var statsRepo = new EfPrinterStatisticsRepository(db);
            ToolheadActivitySnapshot? snapshot = await service.SyncPrinterStatisticsAsync(
                printer,
                new PrintStatsSyncSettings { IncludePrintFarmerJobs = false, ApiTimeoutSeconds = 30 },
                statsRepo,
                new EfToolheadStatisticsRepository(db),
                Mock.Of<IPrintJobStatisticsRepository>(),
                featureGate.Object,
                provider,
                CancellationToken.None);
            await PrintStatsSyncHostedService.CommitAndAcknowledgeAsync(
                statsRepo,
                accumulator,
                snapshot,
                CancellationToken.None);
        }

        await using AppDbContext verify = new(options);
        Toolhead toolhead = await verify.Toolheads.SingleAsync(t => t.Id == toolheadId);
        PrinterStatistics stats = await verify.PrinterStatisticsSet.SingleAsync(s => s.PrinterId == printerId);

        stats.ExternalPrintHours.Should().Be(100.5);
        stats.LastExternalHoursAttributionUtc.Should().NotBeNull();
        stats.LastExternalHoursAttributionUtc!.Value.Should().BeAfter(priorBoundaryUtc);
        toolhead.CumulativePrintHours.Should().BeApproximately(0.5, 0.01,
            "a genuine, physically-plausible increase must still be attributed via observed telemetry");
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_MidSyncFailure_RollsBackPrinterAndContinuesBatch()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid failingPrinterId = Guid.NewGuid();
        Guid healthyPrinterId = Guid.NewGuid();
        Guid failingModelId = Guid.NewGuid();
        Guid healthyModelId = Guid.NewGuid();
        Guid failingToolheadId = Guid.NewGuid();
        Guid healthyToolheadId = Guid.NewGuid();
        Printer failingPrinter = new()
        {
            Id = failingPrinterId,
            Name = "Fails after baseline",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = failingModelId,
            ServerUrl = "http://failing-printer.local"
        };
        Printer healthyPrinter = new()
        {
            Id = healthyPrinterId,
            Name = "Healthy printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = healthyModelId,
            ServerUrl = "http://healthy-printer.local"
        };

        Mock<IPrintersRepository> printers = new(MockBehavior.Strict);
        printers.Setup(repository => repository.GetForStatsSyncRotationAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([failingPrinter, healthyPrinter]);
        printers.Setup(repository => repository.MarkStatsSyncAttemptedAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IPrintJobStatisticsRepository> jobStats = new(MockBehavior.Strict);
        jobStats.Setup(repository => repository.GetByPrinterModelAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid modelId, bool _, DateTime? _, CancellationToken _) =>
                modelId == failingModelId
                    ? Task.FromException<List<PrintJobStatistics>>(new TimeoutException("injected after baseline"))
                    : Task.FromResult(new List<PrintJobStatistics>()));

        Mock<IBackendClient> client = new();
        client.As<ISupportsHistory>()
            .Setup(history => history.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, PrinterCredential? _, CancellationToken _) => new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalPrintTime = (url.Contains("failing", StringComparison.Ordinal) ? 110 : 210) * 3600,
                    TotalJobs = 10
                }
            });
        Mock<IBackendClientFactory> clientFactory = new();
        clientFactory.Setup(factory => factory.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);

        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(gate => gate.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        featureGate.Setup(gate => gate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        ServiceCollection services = new();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IPrintersRepository>(printers.Object);
        services.AddScoped<IPrinterStatisticsRepository, EfPrinterStatisticsRepository>();
        services.AddScoped<IToolheadStatisticsRepository, EfToolheadStatisticsRepository>();
        services.AddSingleton<IPrintJobStatisticsRepository>(jobStats.Object);
        services.AddSingleton<IBackendClientFactory>(clientFactory.Object);
        services.AddSingleton<IOperatorFeatureGate>(featureGate.Object);
        using ServiceProvider provider = services.BuildServiceProvider();

        await using (AsyncServiceScope seedScope = provider.CreateAsyncScope())
        {
            AppDbContext seed = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seed.PrinterStatisticsSet.AddRange(
                new PrinterStatistics
                {
                    Id = Guid.NewGuid(),
                    PrinterId = failingPrinterId,
                    TotalPrintHours = 100,
                    ExternalPrintHours = 100,
                    ExternalBaselineInitializedUtc = DateTime.UtcNow
                },
                new PrinterStatistics
                {
                    Id = Guid.NewGuid(),
                    PrinterId = healthyPrinterId,
                    TotalPrintHours = 200,
                    ExternalPrintHours = 200,
                    ExternalBaselineInitializedUtc = DateTime.UtcNow
                });
            seed.Toolheads.AddRange(
                new Toolhead
                {
                    Id = failingToolheadId,
                    PrinterId = failingPrinterId,
                    Name = "T0",
                    Index = 0,
                    ToolheadType = ToolheadType.Physical
                },
                new Toolhead
                {
                    Id = healthyToolheadId,
                    PrinterId = healthyPrinterId,
                    Name = "T0",
                    Index = 0,
                    ToolheadType = ToolheadType.Physical
                });
            await seed.SaveChangesAsync();
        }

        PrintStatsSyncHostedService service = CreateService(provider);
        await service.SyncPrinterStatisticsAsync(
            new PrintStatsSyncSettings
            {
                IncludePrintFarmerJobs = true,
                MaxPrintersPerIteration = 2,
                ApiTimeoutSeconds = 30
            },
            CancellationToken.None);

        await using AsyncServiceScope verifyScope = provider.CreateAsyncScope();
        AppDbContext verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterStatistics failingStats = await verify.PrinterStatisticsSet
            .AsNoTracking()
            .SingleAsync(stats => stats.PrinterId == failingPrinterId);
        Toolhead failingToolhead = await verify.Toolheads
            .AsNoTracking()
            .SingleAsync(toolhead => toolhead.Id == failingToolheadId);
        PrinterStatistics healthyStats = await verify.PrinterStatisticsSet
            .AsNoTracking()
            .SingleAsync(stats => stats.PrinterId == healthyPrinterId);
        Toolhead healthyToolhead = await verify.Toolheads
            .AsNoTracking()
            .SingleAsync(toolhead => toolhead.Id == healthyToolheadId);

        failingStats.ExternalPrintHours.Should().Be(100,
            "disposing the failed printer scope must discard its tracked baseline advance");
        failingToolhead.CumulativePrintHours.Should().Be(0);
        healthyStats.ExternalPrintHours.Should().Be(210,
            "a later printer must still complete in its independent scope");

        // Issue #711 Finding H1: the healthy printer's external delta advances its printer-wide
        // baseline (ExternalPrintHours 200 -> 210) but is NOT attributed to the toolhead, because
        // the printer does not advertise SupportsPerToolAttribution. The removed equal-split would
        // previously have credited the single physical head with the full 10h.
        healthyToolhead.CumulativePrintHours.Should().Be(0,
            "without SupportsPerToolAttribution per-toolhead wear stays unattributed");
        jobStats.Verify(repository => repository.GetByPrinterModelAsync(
            healthyModelId,
            true,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression coverage for issue #2061 round-2 review (Hicks): the rotation-query test suite
    /// (<c>PrinterRotationQueryTests</c>) proves <see cref="EfPrintersRepository.GetForStatsSyncRotationAsync"/>
    /// and <see cref="EfPrintersRepository.MarkStatsSyncAttemptedAsync"/> rotate correctly in
    /// isolation, but calls <c>MarkStatsSyncAttemptedAsync</c> directly rather than driving
    /// <see cref="PrintStatsSyncHostedService"/> itself — so it would keep passing even if the
    /// hosted service's <c>finally</c> cursor-advance block were deleted entirely. This test closes
    /// that gap: it drives the REAL <see cref="PrintStatsSyncHostedService.SyncPrinterStatisticsAsync(PrintStatsSyncSettings,CancellationToken)"/>
    /// entry point end-to-end, through a real <see cref="EfPrintersRepository"/>, across three
    /// iterations, with EVERY printer's per-model job-statistics lookup throwing on every attempt
    /// (simulating a permanently failing backend/dependency for the entire fleet). If the
    /// hosted service's rotation-cursor <c>finally</c> block were ever removed or short-circuited,
    /// this test would fail because the same two printers would be re-selected on every iteration.
    /// </summary>
    [Fact]
    public async Task SyncPrinterStatisticsAsync_ThreeIterations_AllPrintersAttempted_EvenWhenEveryAttemptFails()
    {
        const int maxPrintersPerIteration = 2;
        const int printerCount = maxPrintersPerIteration * 3;
        string dbName = Guid.NewGuid().ToString("N");

        List<Guid> printerIds = [.. Enumerable.Range(0, printerCount).Select(_ => Guid.NewGuid())];

        Mock<IPrintJobStatisticsRepository> jobStats = new(MockBehavior.Strict);
        jobStats.Setup(repository => repository.GetByPrinterModelAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromException<List<PrintJobStatistics>>(
                new TimeoutException("injected: every printer fails every iteration")));

        Mock<IBackendClient> client = new();
        client.As<ISupportsHistory>()
            .Setup(history => history.GetHistoryTotalsAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals { JobTotals = new JobTotals { TotalPrintTime = 3600, TotalJobs = 1 } });
        Mock<IBackendClientFactory> clientFactory = new();
        clientFactory.Setup(factory => factory.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);

        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(gate => gate.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        featureGate.Setup(gate => gate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        ServiceCollection services = new();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddScoped<IPrintersRepository>(sp =>
            new EfPrintersRepository(sp.GetRequiredService<AppDbContext>(), NoOpSensitiveDataProtector.Instance));
        services.AddScoped<IPrinterStatisticsRepository, EfPrinterStatisticsRepository>();
        services.AddScoped<IToolheadStatisticsRepository, EfToolheadStatisticsRepository>();
        services.AddSingleton<IPrintJobStatisticsRepository>(jobStats.Object);
        services.AddSingleton<IBackendClientFactory>(clientFactory.Object);
        services.AddSingleton<IOperatorFeatureGate>(featureGate.Object);
        using ServiceProvider provider = services.BuildServiceProvider();

        await using (AsyncServiceScope seedScope = provider.CreateAsyncScope())
        {
            AppDbContext seed = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (Guid printerId in printerIds)
            {
                seed.Printers.Add(new Printer
                {
                    Id = printerId,
                    Name = $"Printer {printerId}",
                    Backend = (int)PrinterBackend.Moonraker,
                    ModelId = Guid.NewGuid(),
                    ServerUrl = $"http://printer-{printerId}.local"
                });
            }
            await seed.SaveChangesAsync();
        }

        PrintStatsSyncHostedService service = CreateService(provider);
        var settings = new PrintStatsSyncSettings
        {
            IncludePrintFarmerJobs = true,
            MaxPrintersPerIteration = maxPrintersPerIteration,
            ApiTimeoutSeconds = 30
        };

        HashSet<Guid> attemptedAcrossAllIterations = [];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await service.SyncPrinterStatisticsAsync(settings, CancellationToken.None);

            await using AsyncServiceScope verifyScope = provider.CreateAsyncScope();
            AppDbContext verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<Guid> attemptedThisIteration = await verify.PrinterServiceStates
                .AsNoTracking()
                .Where(s => s.LastStatsSyncAttemptedAt != null && !attemptedAcrossAllIterations.Contains(s.PrinterId))
                .Select(s => s.PrinterId)
                .ToListAsync();

            attemptedThisIteration.Should().HaveCount(
                maxPrintersPerIteration,
                $"iteration {iteration} must advance the cursor for exactly {maxPrintersPerIteration} previously-unattempted printers, " +
                "even though every printer's sync fails");
            attemptedAcrossAllIterations.UnionWith(attemptedThisIteration);
        }

        attemptedAcrossAllIterations.Should().BeEquivalentTo(
            printerIds,
            "all printers must be attempted exactly once across the 3 iterations despite every attempt failing " +
            "(issue #2061 round-2 review: the rotation cursor must advance through the hosted service's real " +
            "finally-block wiring, not just via a direct repository call)");
    }

    private static PrintStatsSyncHostedService CreateService(IServiceProvider provider)
    {
        return new PrintStatsSyncHostedService(
            provider,
            Mock.Of<ILogger<PrintStatsSyncHostedService>>(),
            Mock.Of<IOptionsMonitor<PrintStatsSyncSettings>>(),
            Mock.Of<IBackgroundServiceMonitor>());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp = checked(_timestamp + elapsed.Ticks);
    }

    /// <summary>
    /// Null-op protector — this test only exercises rotation across the hosted service, not
    /// encryption/decryption of printer credentials.
    /// </summary>
    private sealed class NoOpSensitiveDataProtector : ISensitiveDataProtector
    {
        public static NoOpSensitiveDataProtector Instance { get; } = new();
        public string? Protect(string? plainText) => plainText;
        public string? Unprotect(string? protectedText) => protectedText;
    }
}
