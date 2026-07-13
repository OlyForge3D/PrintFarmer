using Farm.Infrastructure.Services.Maintenance;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

public class ToolheadActivityAccumulatorTests
{
    [Fact]
    public void Sample_ConsecutivePrintingSamples_AccumulatesActiveAndWindowSeconds()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, double>(0, 30));
        snapshot.RecognizedSeconds.Should().BeApproximately(30, 0.0001);
        snapshot.WindowSeconds.Should().BeApproximately(30, 0.0001);
    }

    [Fact]
    public void Sample_ToolSwitchWithinWindow_SplitsSecondsAcrossBothTools()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(75));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(25));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds[0].Should().BeApproximately(75, 0.0001);
        snapshot.ActiveSeconds[1].Should().BeApproximately(25, 0.0001);
        snapshot.WindowSeconds.Should().BeApproximately(100, 0.0001);
    }

    [Fact]
    public void PeekActiveSeconds_WithoutAcknowledgment_IsNonDestructive()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot first = accumulator.PeekActiveSeconds(printerId);
        ToolheadActivitySnapshot second = accumulator.PeekActiveSeconds(printerId);

        second.ActiveSeconds.Should().BeEquivalentTo(first.ActiveSeconds);
        second.WindowSeconds.Should().Be(first.WindowSeconds);
    }

    [Fact]
    public void AckActiveSecondsThrough_Snapshot_PreservesSamplesRecordedAfterPeek()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        ToolheadActivitySnapshot first = accumulator.PeekActiveSeconds(printerId);

        clock.Advance(TimeSpan.FromSeconds(45));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        accumulator.AckActiveSecondsThrough(first);

        ToolheadActivitySnapshot pending = accumulator.PeekActiveSeconds(printerId);
        pending.ActiveSeconds.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, double>(0, 45));
        pending.WindowSeconds.Should().BeApproximately(45, 0.0001);
    }

    [Fact]
    public void AckActiveSecondsThrough_CurrentSnapshot_ClearsOnlyAcknowledgedWindow()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        accumulator.AckActiveSecondsThrough(snapshot);

        ToolheadActivitySnapshot pending = accumulator.PeekActiveSeconds(printerId);
        pending.ActiveSeconds.Should().BeEmpty();
        pending.RecognizedSeconds.Should().Be(0);
        pending.WindowSeconds.Should().Be(0);
    }

    [Fact]
    public void AckActiveSecondsThrough_CurrentSnapshot_CompactsAcknowledgedBuckets()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(15));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        accumulator.AckActiveSecondsThrough(snapshot);

        ToolheadActivitySnapshot pending = accumulator.PeekActiveSeconds(printerId);
        pending.ActiveSeconds.Should().BeEmpty();
        pending.CumulativeActiveSeconds.Should().BeEmpty();
        pending.WindowSeconds.Should().Be(0);
    }

    [Fact]
    public void Sample_SegmentLongerThanCap_IsUnrecognizedButIncludedInWindow()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromSeconds(10));
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromHours(1));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().BeEmpty();
        snapshot.RecognizedSeconds.Should().Be(0);
        snapshot.WindowSeconds.Should().BeApproximately(3600, 0.0001);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, -1)]
    public void Sample_UnrecognizedSegment_AccruesWindowWithoutToolSeconds(bool isPrinting, int toolIndex)
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, toolIndex, isPrinting);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, toolIndex, isPrinting);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().BeEmpty();
        snapshot.WindowSeconds.Should().BeApproximately(30, 0.0001);
    }

    [Fact]
    public void SampleKnownIdle_ConfirmedIdleSegment_TracksKnownIdleSecondsAlongsideWindow()
    {
        // Issue #711, round-19 V19-1/H19-1: a confirmed-idle segment (fresh telemetry showing the
        // printer is not printing) must accrue both the window total and the known-idle total in
        // parallel, while never creating an active-seconds bucket for any tool.
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();

        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromSeconds(45));
        accumulator.SampleKnownIdle(printerId);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().BeEmpty();
        snapshot.RecognizedSeconds.Should().Be(0);
        snapshot.WindowSeconds.Should().BeApproximately(45, 0.0001);
        snapshot.KnownIdleSeconds.Should().BeApproximately(45, 0.0001);
    }

    [Fact]
    public void Sample_TransitionFromKnownIdleToPrinting_CreditsPriorSegmentAsIdleNotToTheNewTool()
    {
        // The segment BEFORE a state-changing Sample/SampleKnownIdle call is attributed to whatever
        // state the PREVIOUS call established, so a known-idle-to-printing transition must credit the
        // preceding elapsed time as known-idle, not to the newly-observed tool.
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromHours(1));
        Guid printerId = Guid.NewGuid();

        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromMinutes(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromMinutes(10));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, double>(0, 600));
        snapshot.KnownIdleSeconds.Should().BeApproximately(1800, 0.0001);
        snapshot.WindowSeconds.Should().BeApproximately(2400, 0.0001);
    }

    [Fact]
    public void Sample_TransitionFromPrintingToKnownIdle_DoesNotRetroactivelyReclassifyPriorPrintingSegment()
    {
        // Symmetric to the above: the printing segment that occurred BEFORE the printer was
        // confirmed idle must remain credited as printing; only the segment AFTER the
        // SampleKnownIdle call is excluded from the numerator and the effective coverage denominator.
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromHours(1));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromMinutes(15));
        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromMinutes(20));
        accumulator.SampleKnownIdle(printerId);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, double>(0, 900));
        snapshot.KnownIdleSeconds.Should().BeApproximately(1200, 0.0001);
        snapshot.WindowSeconds.Should().BeApproximately(2100, 0.0001);
    }

    [Fact]
    public void AckActiveSecondsThrough_Snapshot_PreservesKnownIdleSecondsRecordedAfterPeek()
    {
        // Mirrors AckActiveSecondsThrough_Snapshot_PreservesSamplesRecordedAfterPeek but for the
        // known-idle bucket: acknowledging an earlier snapshot must not discard known-idle seconds
        // accrued after that snapshot was taken.
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.SampleKnownIdle(printerId);
        ToolheadActivitySnapshot first = accumulator.PeekActiveSeconds(printerId);

        clock.Advance(TimeSpan.FromSeconds(45));
        accumulator.SampleKnownIdle(printerId);
        accumulator.AckActiveSecondsThrough(first);

        ToolheadActivitySnapshot pending = accumulator.PeekActiveSeconds(printerId);
        pending.KnownIdleSeconds.Should().BeApproximately(45, 0.0001);
        pending.WindowSeconds.Should().BeApproximately(45, 0.0001);
    }

    [Fact]
    public void AckActiveSecondsThrough_CurrentSnapshot_ClearsKnownIdleSecondsToo()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.SampleKnownIdle(printerId);
        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        accumulator.AckActiveSecondsThrough(snapshot);

        ToolheadActivitySnapshot pending = accumulator.PeekActiveSeconds(printerId);
        pending.KnownIdleSeconds.Should().Be(0);
        pending.WindowSeconds.Should().Be(0);
    }

    [Fact]
    public void Sample_OutOfRangeIndexes_AreTreatedAsUnknownAndDoNotCreateBuckets()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();

        for (int index = 32; index < 96; index++)
        {
            accumulator.Sample(printerId, activeToolIndex: index, isPrinting: true);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().BeEmpty();
        snapshot.CumulativeActiveSeconds.Should().BeEmpty();
        snapshot.WindowSeconds.Should().BeApproximately(63, 0.0001);
    }

    [Fact]
    public void SampleKnownIdle_SegmentExceedingMaxSegment_IsNotCreditedAsKnownIdle()
    {
        // r22: a telemetry outage between an idle sample and the next sample (any state) must NOT
        // be credited as known-idle when the gap exceeds _maxSegment. The gap still accrues to the
        // window, becoming unknown coverage that properly dilutes the coverage denominator.
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromSeconds(10));
        Guid printerId = Guid.NewGuid();

        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromMinutes(5));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.KnownIdleSeconds.Should().Be(0,
            "a gap exceeding maxSegment cannot be confirmed idle — it is a telemetry outage");
        snapshot.WindowSeconds.Should().BeApproximately(300, 0.0001,
            "the gap still accrues to the window as unknown coverage");
    }

    [Fact]
    public void SampleKnownIdle_GapExceedingMaxSegmentBetweenTwoIdleSamples_IsNotCreditedAsKnownIdle()
    {
        // r22: even when both endpoints of a gap are idle, a gap exceeding _maxSegment is a
        // telemetry outage — we cannot confirm idle status during the unobserved interval.
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromSeconds(10));
        Guid printerId = Guid.NewGuid();

        accumulator.SampleKnownIdle(printerId);
        clock.Advance(TimeSpan.FromMinutes(5));
        accumulator.SampleKnownIdle(printerId);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.KnownIdleSeconds.Should().Be(0);
        snapshot.WindowSeconds.Should().BeApproximately(300, 0.0001);
    }

    [Fact]
    public void SampleKnownIdle_SubCapSegmentLoop_FullyCreditedAsKnownIdle()
    {
        // r22: sub-cap idle segments (the normal production cadence — idle is re-sampled every ≤60s,
        // well under the 2-minute freshness cap) must all be credited as known-idle. This proves the
        // freshness cap does not break the healthy-connection case.
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) =
            NewAccumulator(TimeSpan.FromMinutes(2));
        Guid printerId = Guid.NewGuid();

        accumulator.SampleKnownIdle(printerId);
        for (int i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            accumulator.SampleKnownIdle(printerId);
        }

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.KnownIdleSeconds.Should().BeApproximately(3600, 0.0001,
            "all 60 sub-cap idle segments must be fully credited");
        snapshot.WindowSeconds.Should().BeApproximately(3600, 0.0001);

        // Now add printing and verify the full idle is properly excluded from the denominator
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(60));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot withPrint = accumulator.PeekActiveSeconds(printerId);

        withPrint.KnownIdleSeconds.Should().BeApproximately(3600, 0.0001);
        withPrint.ActiveSeconds[0].Should().BeApproximately(60, 0.0001);
        double effectiveWindow = withPrint.WindowSeconds - withPrint.KnownIdleSeconds;
        effectiveWindow.Should().BeApproximately(60, 0.0001,
            "effective window after subtracting known-idle equals the print segment");
    }

    [Fact]
    public void Sample_DecreasingMonotonicTimestamp_IsIgnoredAndDoesNotRewindWindow()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Set(TimeSpan.FromSeconds(100));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Set(TimeSpan.FromSeconds(50));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        clock.Set(TimeSpan.FromSeconds(130));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(printerId);

        snapshot.ActiveSeconds.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, double>(0, 130));
        snapshot.WindowSeconds.Should().BeApproximately(130, 0.0001);
    }

    [Fact]
    public void Reset_StaleAcknowledgmentCannotConsumeRecreatedPrinterState()
    {
        (ToolheadActivityAccumulator accumulator, ManualTimeProvider clock) = NewAccumulator();
        Guid printerId = Guid.NewGuid();
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true);
        ToolheadActivitySnapshot stale = accumulator.PeekActiveSeconds(printerId);

        accumulator.Reset(printerId);
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        clock.Advance(TimeSpan.FromSeconds(20));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true);
        accumulator.AckActiveSecondsThrough(stale);

        ToolheadActivitySnapshot current = accumulator.PeekActiveSeconds(printerId);
        current.ActiveSeconds.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, double>(1, 20));
    }

    [Fact]
    public void PeekActiveSeconds_UnknownPrinter_ReturnsEmptySnapshot()
    {
        var accumulator = new ToolheadActivityAccumulator();

        ToolheadActivitySnapshot snapshot = accumulator.PeekActiveSeconds(Guid.NewGuid());

        snapshot.ActiveSeconds.Should().BeEmpty();
        snapshot.WindowSeconds.Should().Be(0);
    }

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

        public void Set(TimeSpan elapsedSinceStart) => _timestamp = elapsedSinceStart.Ticks;
    }
}
