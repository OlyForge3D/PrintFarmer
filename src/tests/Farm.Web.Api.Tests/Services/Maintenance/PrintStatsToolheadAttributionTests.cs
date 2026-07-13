using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Services.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

public class PrintStatsToolheadAttributionTests
{
    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_ExternalSyncFailed_DoesNotIncrementToolheads()
    {
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            Guid.NewGuid(),
            statsExisted: true,
            externalSyncSuccess: false,
            perToolMaintenanceEnabled: true,
            externalDelta: 12,
            repository.Object,
            CancellationToken.None);

        credited.Should().BeEmpty();
        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_UnchangedExternalHoursAcrossCycles_DoesNotIncrementToolheads()
    {
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);
        Guid printerId = Guid.NewGuid();

        for (int cycle = 0; cycle < 2; cycle++)
        {
            IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
                printerId,
                statsExisted: true,
                externalSyncSuccess: true,
                perToolMaintenanceEnabled: true,
                externalDelta: 0,
                repository.Object,
                CancellationToken.None);

            credited.Should().BeEmpty();
        }

        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_FeatureDisabled_DoesNotIncrementToolheads()
    {
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            Guid.NewGuid(),
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: false,
            externalDelta: 4,
            repository.Object,
            CancellationToken.None);

        credited.Should().BeEmpty();
        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IncrementActiveToolheadHoursAsync_MultiplePhysicalToolheads_SplitsDeltaEqually()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead primary = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 10);
        Toolhead secondary = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 4);
        Toolhead mmuGate = CreateToolhead(printerId, ToolheadType.MmuGate, index: 2, cumulativeHours: 7);
        db.Toolheads.AddRange(primary, secondary, mmuGate);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);

        IReadOnlyList<Guid> credited = await repository.IncrementActiveToolheadHoursAsync(
            printerId,
            deltaHours: 6,
            CancellationToken.None);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([primary.Id, secondary.Id]);
        primary.CumulativePrintHours.Should().Be(13);
        secondary.CumulativePrintHours.Should().Be(7);
        mmuGate.CumulativePrintHours.Should().Be(7);
    }

    [Fact]
    public async Task ApplyToolheadHoursAsync_ExclusiveActiveTool_CreditsOnlyThatToolhead()
    {
        // Issue #711 round-7 Finding 3: with T0 doing all the printing, only T0 should accrue wear;
        // T1 must NOT be advanced by an equal split.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 10);
        Toolhead t1 = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 4);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);

        ToolheadHourAttribution attribution = ToolheadHourAttribution.FromWeights(
            new Dictionary<Guid, double> { [t0.Id] = 6, [t1.Id] = 0 });
        attribution.IsApproximated.Should().BeFalse();

        IReadOnlyList<Guid> credited = await repository.ApplyToolheadHoursAsync(
            printerId,
            attribution,
            CancellationToken.None);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([t0.Id]);
        t0.CumulativePrintHours.Should().Be(16);
        t1.CumulativePrintHours.Should().Be(4, "an idle toolhead must not accrue wear");
    }

    [Fact]
    public async Task ApplyToolheadHoursAsync_MixedActivity_SplitsProportionally()
    {
        // Issue #711 round-7 Finding 3: proportional per-tool weights credit each toolhead by its
        // actual share of the work.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 0);
        Toolhead t1 = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 0);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);

        ToolheadHourAttribution attribution = ToolheadHourAttribution.FromWeights(
            new Dictionary<Guid, double> { [t0.Id] = 7.0, [t1.Id] = 3.0 });

        IReadOnlyList<Guid> credited = await repository.ApplyToolheadHoursAsync(
            printerId,
            attribution,
            CancellationToken.None);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([t0.Id, t1.Id]);
        t0.CumulativePrintHours.Should().BeApproximately(7.0, 0.0001);
        t1.CumulativePrintHours.Should().BeApproximately(3.0, 0.0001);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_NoTelemetry_EqualSplitEmitsApproximationDiagnostic()
    {
        // Issue #711 round-7 Finding 3: when no per-tool consumption telemetry is available the
        // external delta is split equally, but an operator-visible diagnostic must be emitted so it
        // is clear the per-toolhead wear is only an estimate.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 0);
        Toolhead t1 = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 0);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);
        Mock<ILogger> logger = new();

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            externalDelta: 8,
            repository,
            CancellationToken.None,
            logger.Object);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([t0.Id, t1.Id]);
        t0.CumulativePrintHours.Should().BeApproximately(4.0, 0.0001);
        t1.CumulativePrintHours.Should().BeApproximately(4.0, 0.0001);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("approximated")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_FreshActiveToolTelemetry_CreditsOnlyActiveToolhead()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 0);
        Toolhead t1 = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 0);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        Mock<IPrinterStatusCacheReader> statusCache = new(MockBehavior.Strict);
        statusCache.Setup(cache => cache.GetSnapshot(printerId)).Returns(
            new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(
                    printerId,
                    IsOnline: true,
                    State: "printing",
                    MmuStatus: new MmuStatusDto(
                        Enabled: true,
                        IsHomed: true,
                        ActiveTool: 1,
                        ActiveGate: 1,
                        FilamentState: "Loaded",
                        Action: "Idle",
                        NumGates: 2,
                        HasBypass: false,
                        EndlessSpool: false,
                        ClogDetection: false,
                        Gates: [])),
                DateTime.UtcNow));

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            externalDelta: 8,
            repository,
            CancellationToken.None,
            statusCache: statusCache.Object);
        await db.SaveChangesAsync();

        credited.Should().Equal(t1.Id);
        t0.CumulativePrintHours.Should().Be(0, "idle heads must not accrue wear");
        t1.CumulativePrintHours.Should().Be(8);
    }

    [Fact]
    public void FromWeights_PartialKnownActivity_LeavesUnknownResidualUncredited()
    {
        Guid knownToolhead = Guid.NewGuid();

        ToolheadHourAttribution attribution = ToolheadHourAttribution.FromWeights(
            new Dictionary<Guid, double> { [knownToolhead] = 0.4 },
            sourceHours: 10);

        attribution.Weights.Should().ContainSingle().Which.Value.Should().Be(0.4);
        attribution.Hours.Should().ContainSingle().Which.Value.Should().Be(4);
        attribution.TotalHours.Should().Be(4);
        attribution.SourceHours.Should().Be(10);
        attribution.IsApproximated.Should().BeFalse();
    }

    private static Toolhead CreateToolhead(
        Guid printerId,
        ToolheadType type,
        int index,
        double cumulativeHours)
    {
        return new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = $"Toolhead {index}",
            Index = index,
            ToolheadType = type,
            CumulativePrintHours = cumulativeHours
        };
    }
}
