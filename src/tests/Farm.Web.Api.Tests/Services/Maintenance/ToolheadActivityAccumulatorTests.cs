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
