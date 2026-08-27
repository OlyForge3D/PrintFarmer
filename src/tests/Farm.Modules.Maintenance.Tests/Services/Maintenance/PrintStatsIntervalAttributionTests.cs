using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Modules.Maintenance.Services.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Modules.Maintenance.Tests.Services.Maintenance;

public class PrintStatsIntervalAttributionTests
{
    [Fact]
    public async Task IntervalTelemetry_FullCoverage_CreditsFullDeltaToObservedTool()
    {
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(120));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

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
        t1.CumulativePrintHours.Should().Be(0);
    }

    [Fact]
    public async Task IntervalTelemetry_FullCoverageWithToolSwitch_SplitsDeltaProportionally()
    {
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(75));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(25));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);

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
    public async Task IntervalTelemetry_PartialCoverage_CreditsOnlyObservedFractionOfExternalDelta()
    {
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(120));
        accumulator.Sample(printerId, activeToolIndex: null, isPrinting: true);
        clock.Advance(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(120));
        accumulator.Sample(printerId, activeToolIndex: null, isPrinting: true);

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
        t0.CumulativePrintHours.Should().BeApproximately(120d / 3600d, 0.0001);
        t1.CumulativePrintHours.Should().Be(0);
    }

    [Fact]
    public async Task IntervalTelemetry_ZeroDeltaCyclesThenCompletion_RetainsAllBaselineScopedSamples()
    {
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromHours(1));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        for (int cycle = 0; cycle < 6; cycle++)
        {
            clock.Advance(TimeSpan.FromMinutes(30));
            accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
            (await AttributeAsync(printerId, externalDelta: 0, repository, accumulator)).Should().BeEmpty();
        }

        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        clock.Advance(TimeSpan.FromMinutes(30));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        (await AttributeAsync(printerId, externalDelta: 0, repository, accumulator)).Should().BeEmpty();
        clock.Advance(TimeSpan.FromMinutes(30));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        ToolheadActivitySnapshot completionSnapshot = accumulator.PeekActiveSeconds(printerId);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 4,
            repository,
            CancellationToken.None,
            activitySnapshot: completionSnapshot);
        await db.SaveChangesAsync();
        accumulator.AckActiveSecondsThrough(completionSnapshot);

        credited.Should().BeEquivalentTo([t0.Id, t1.Id]);
        t0.CumulativePrintHours.Should().BeApproximately(3, 0.0001);
        t1.CumulativePrintHours.Should().BeApproximately(1, 0.0001);
        accumulator.PeekActiveSeconds(printerId).WindowSeconds.Should().Be(0);
    }

    [Fact]
    public async Task CommitAndAcknowledgeAsync_SaveFails_RetainsSnapshotForRetry()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(120));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        ToolheadActivitySnapshot firstAttempt = accumulator.PeekActiveSeconds(printerId);
        Mock<IPrinterStatisticsRepository> statistics = new(MockBehavior.Strict);
        statistics
            .SetupSequence(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("simulated commit failure"))
            .Returns(Task.CompletedTask);

        Func<Task> failedCommit = () => PrintStatsSyncHostedService.CommitAndAcknowledgeAsync(
            statistics.Object,
            accumulator,
            firstAttempt,
            CancellationToken.None);
        await failedCommit.Should().ThrowAsync<DbUpdateException>();

        ToolheadActivitySnapshot retry = accumulator.PeekActiveSeconds(printerId);
        retry.ActiveSeconds.Should().BeEquivalentTo(firstAttempt.ActiveSeconds);
        retry.WindowSeconds.Should().Be(firstAttempt.WindowSeconds);

        await PrintStatsSyncHostedService.CommitAndAcknowledgeAsync(
            statistics.Object,
            accumulator,
            retry,
            CancellationToken.None);

        accumulator.PeekActiveSeconds(printerId).WindowSeconds.Should().Be(0);
        statistics.Verify(
            repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Attribution_FeatureDisabled_DoesNotDiscardPendingTelemetry()
    {
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);
        Guid printerId = Guid.NewGuid();
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(120));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

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
        accumulator.PeekActiveSeconds(printerId).RecognizedSeconds.Should().BeApproximately(120, 0.0001);
    }

    [Fact]
    public async Task IntervalTelemetry_KnownIdleThenFullPrint_ExcludesIdleFromCoverageDenominator()
    {
        // Issue #711, round-19 V19-1/H19-1: known-idle seconds (a printer CONFIRMED not printing via
        // fresh telemetry) must be excluded from both the numerator and the coverage denominator.
        // Before the fix, 23h of confirmed idle followed by 1h of fully-observed printing computed
        // coverage = 1h / 24h ~= 0.04, destroying 96% of the attributable external-history delta.
        //
        // r22: the idle duration must be accumulated in sub-cap segments as production does (idle is
        // re-sampled every ≤60s, well under the 2-minute freshness cap). A single over-cap idle
        // segment is a telemetry outage, not confirmed idle, and would not be credited after the r22
        // freshness-cap fix. Here we use 1-hour cadence against a 2-hour maxSegment for test brevity.
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator(TimeSpan.FromHours(2));

        // 23h of confirmed idle, accumulated in sub-cap 1-hour segments
        accumulator.SampleKnownIdle(printerId);
        for (int i = 0; i < 23; i++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            accumulator.SampleKnownIdle(printerId);
        }

        // 1h of fully-observed printing
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromHours(1));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 1,
            repository,
            CancellationToken.None,
            activityAccumulator: accumulator);
        await db.SaveChangesAsync();

        credited.Should().Equal(t0.Id);
        t0.CumulativePrintHours.Should().BeApproximately(1, 0.0001,
            "the full observed print hour must be credited, not diluted by the preceding 23h of confirmed idle");
        t1.CumulativePrintHours.Should().Be(0);
    }

    [Fact]
    public async Task IntervalTelemetry_IdleOutageThenPrint_OutageIsUnknownCoverage()
    {
        // r22: a telemetry outage bracketed by an idle sample and a subsequent print sample must NOT
        // be credited as known-idle. The gap exceeds maxSegment, so it becomes unknown coverage in
        // the denominator, preventing the post-gap print from receiving full external-delta credit.
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        db.Toolheads.Add(t0);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromMinutes(2));

        // Fresh idle sample, then 5-minute outage (>> 2 min maxSegment), then 2 min print
        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromMinutes(5));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromMinutes(2));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);
        snapshot.KnownIdleSeconds.Should().Be(0,
            "a gap exceeding maxSegment cannot be confirmed idle");
        snapshot.WindowSeconds.Should().BeApproximately(420, 0.0001,
            "the full 7-minute window is tracked");

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 1,
            repository,
            CancellationToken.None,
            activityAccumulator: accumulator);
        await db.SaveChangesAsync();

        credited.Should().Equal(t0.Id);
        t0.CumulativePrintHours.Should().BeApproximately(2.0 / 7.0, 0.01,
            "the 5-minute outage must not be treated as known-idle; it stays in the denominator " +
            "as unknown coverage, so only 2/7 of the delta is attributed");
    }

    [Fact]
    public async Task IntervalTelemetry_IdleOutageThenIdle_OutageIsUnknownNotKnownIdle()
    {
        // r22: a gap exceeding maxSegment between two idle samples is a telemetry outage, not
        // confirmed idle. The gap accrues to the window but not to known-idle seconds.
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        db.Toolheads.Add(t0);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromMinutes(2));

        // Two idle samples separated by a 10-minute gap (>> 2 min maxSegment)
        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromMinutes(10));
        accumulator.SampleKnownIdle(printerId);

        // Then 2 min of printing to have something to attribute
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromMinutes(2));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);
        snapshot.KnownIdleSeconds.Should().Be(0,
            "neither the outage gap nor the zero-elapsed boundary segment should be known-idle");
        snapshot.WindowSeconds.Should().BeApproximately(720, 0.0001);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 1,
            repository,
            CancellationToken.None,
            activityAccumulator: accumulator);
        await db.SaveChangesAsync();

        credited.Should().Equal(t0.Id);
        t0.CumulativePrintHours.Should().BeApproximately(2.0 / 12.0, 0.01,
            "the 10-minute outage between idle samples is unknown coverage in the denominator");
    }

    [Fact]
    public async Task IntervalTelemetry_UnmappedToolSegment_CountsTowardDenominatorOnlyNotIdleOrNumerator()
    {
        // Issue #711, round-19 V19-1/H19-1: printing with an unrecognized/unmapped tool index (H17-3
        // safety -- e.g. an MMU state Farm cannot resolve, or an out-of-range backend tool index) is
        // genuinely "unknown coverage": it must dilute the coverage denominator (unlike known-idle,
        // which is excluded entirely) but must never be credited to any specific toolhead's numerator.
        await using AppDbContext db = NewDb();
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, index: 0);
        Toolhead t1 = CreateToolhead(printerId, index: 1);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        EfToolheadStatisticsRepository repository = new(db);
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator(TimeSpan.FromHours(2));

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromHours(1));
        // Index 99 is out of the accumulator's tracked range and normalizes to "unmapped" -- printing
        // continued, but Farm cannot resolve which physical tool is responsible for this segment.
        accumulator.Sample(printerId, activeToolIndex: 99, isPrinting: true);
        clock.Advance(TimeSpan.FromMinutes(30));
        accumulator.Sample(printerId, activeToolIndex: 99, isPrinting: true);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 1.5,
            repository,
            CancellationToken.None,
            activityAccumulator: accumulator);
        await db.SaveChangesAsync();

        credited.Should().Equal(t0.Id);
        t0.CumulativePrintHours.Should().BeApproximately(1, 0.0001,
            "the unmapped 30-minute segment dilutes coverage via the denominator but is never credited " +
            "to any specific toolhead");
        t1.CumulativePrintHours.Should().Be(0);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_NotEligibleForAttribution_ReturnsEmptyWithoutTouchingRepository()
    {
        // Issue #711, round-17/19: H17-1's restart-gap safety is preserved through the V19-1/H19-1
        // coverage-denominator fix. When the caller has no persisted attribution boundary (a fresh
        // baseline / restart gap), it passes externalSyncSuccess=false for this cycle; no hours may be
        // attributed regardless of how much telemetry the accumulator has pending, and the repository
        // (a strict mock with zero setups) must never be touched.
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);
        Guid printerId = Guid.NewGuid();
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(120));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: false,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 8,
            repository.Object,
            CancellationToken.None,
            activityAccumulator: accumulator);

        credited.Should().BeEmpty();
        accumulator.PeekActiveSeconds(printerId).RecognizedSeconds.Should().BeApproximately(120, 0.0001,
            "pending telemetry must survive an ineligible cycle untouched for the next real attempt");
    }

    private static Task<IReadOnlyList<Guid>> AttributeAsync(
        Guid printerId,
        double externalDelta,
        IToolheadStatisticsRepository repository,
        IToolheadActivityAccumulator accumulator) =>
        PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta,
            repository,
            CancellationToken.None,
            activityAccumulator: accumulator);

    private static AppDbContext NewDb()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static Toolhead CreateToolhead(Guid printerId, int index) => new()
    {
        Id = Guid.NewGuid(),
        PrinterId = printerId,
        Name = $"Toolhead {index}",
        Index = index,
        ToolheadType = ToolheadType.Physical
    };

    private static (ToolheadActivityAccumulator Accumulator, ManualTimeProvider Clock) NewAccumulator(
        TimeSpan? maxSegment = null)
    {
        var clock = new ManualTimeProvider();
        return (new ToolheadActivityAccumulator(maxSegment ?? TimeSpan.FromMinutes(10), clock), clock);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp = checked(_timestamp + elapsed.Ticks);
    }
}
