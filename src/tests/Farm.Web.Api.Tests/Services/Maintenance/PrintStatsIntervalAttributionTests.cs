using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Services.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

/// <summary>
/// End-to-end tests for interval-aware per-tool attribution (issue #711, round-14). They exercise the
/// full path from the real <see cref="ToolheadActivityAccumulator"/> through
/// <see cref="PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync"/> and the real
/// <see cref="EfToolheadStatisticsRepository"/> against an in-memory database, proving the external
/// history delta is distributed in proportion to the active-tool time actually observed over the sync
/// interval — never equal-split.
/// </summary>
public class PrintStatsIntervalAttributionTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task IntervalTelemetry_ToolZeroActiveWholeInterval_CreditsAllDeltaToToolZero()
    {
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0, cumulativeHours: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1, cumulativeHours: 0);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);

        // Only T0 printed over the interval: two consecutive printing samples on tool 0.
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(120));

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 8,
            repository,
            CancellationToken.None,
            activityAccumulator: accumulator);
        await db.SaveChangesAsync();

        credited.Should().Equal(t0.Id);
        t0.CumulativePrintHours.Should().BeApproximately(8, 0.0001);
        t1.CumulativePrintHours.Should().Be(0, "an idle head must not accrue wear even with telemetry");
    }

    [Fact]
    public async Task IntervalTelemetry_MixedActivity_SplitsDeltaProportionally()
    {
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0, cumulativeHours: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1, cumulativeHours: 0);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);

        // T0 prints 75s, then a switch to T1 for 25s → 75%/25% of the interval.
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(75));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true, Base.AddSeconds(75));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true, Base.AddSeconds(100));

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 8,
            repository,
            CancellationToken.None,
            activityAccumulator: accumulator);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([t0.Id, t1.Id]);
        t0.CumulativePrintHours.Should().BeApproximately(6, 0.0001);
        t1.CumulativePrintHours.Should().BeApproximately(2, 0.0001);
    }

    [Fact]
    public async Task IntervalTelemetry_TakesPrecedenceOverLiveSnapshot()
    {
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0, cumulativeHours: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1, cumulativeHours: 0);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);

        // Interval telemetry says T0 did all the work this cycle.
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(120));

        // The live snapshot only reflects the latest instant (T1). The interval-aware path must win,
        // so the single-sample snapshot fallback is never consulted.
        Mock<IPrinterStatusCacheReader> statusCache = new(MockBehavior.Strict);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 8,
            repository,
            CancellationToken.None,
            statusCache: statusCache.Object,
            activityAccumulator: accumulator);
        await db.SaveChangesAsync();

        credited.Should().Equal(t0.Id);
        t0.CumulativePrintHours.Should().BeApproximately(8, 0.0001);
        t1.CumulativePrintHours.Should().Be(0);
        statusCache.Verify(cache => cache.GetSnapshot(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Attribution_DrainsAccumulatorEvenWhenFeatureDisabled()
    {
        // The accumulator is drained at the top of every cycle so its in-memory window stays bounded
        // to ~one interval even when this cycle attributes nothing. Discarding an unused drain
        // fabricates no wear because only real telemetry seconds ever accumulate.
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);
        Guid printerId = Guid.NewGuid();

        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(120));

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: false,
            supportsPerToolAttribution: true,
            externalDelta: 8,
            repository.Object,
            CancellationToken.None,
            activityAccumulator: accumulator);

        credited.Should().BeEmpty();
        // Draining again returns empty: the disabled-feature cycle still flushed the window.
        accumulator.DrainActiveSeconds(printerId).Should().BeEmpty();
        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AppDbContext NewDb()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static Toolhead CreateToolhead(Guid printerId, int index, double cumulativeHours)
    {
        return new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = $"Toolhead {index}",
            Index = index,
            ToolheadType = ToolheadType.Physical,
            CumulativePrintHours = cumulativeHours
        };
    }
}
