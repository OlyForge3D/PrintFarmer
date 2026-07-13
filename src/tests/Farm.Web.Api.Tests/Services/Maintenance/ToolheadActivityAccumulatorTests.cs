using Farm.Infrastructure.Services.Maintenance;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

/// <summary>
/// Unit tests for the interval-aware per-tool activity accumulator (issue #711, round-14). These
/// prove the accumulator only ever credits real, printing, in-window active-tool time and never
/// fabricates wear.
/// </summary>
public class ToolheadActivityAccumulatorTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sample_ConsecutivePrintingSamples_AccumulatesActiveSecondsForActiveTool()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(30));

        IReadOnlyDictionary<int, double> drained = accumulator.DrainActiveSeconds(printerId);

        drained.Should().ContainKey(0);
        drained[0].Should().BeApproximately(30, 0.0001);
        drained.Should().HaveCount(1);
    }

    [Fact]
    public void Sample_ToolSwitchWithinInterval_SplitsSecondsAcrossBothTools()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        // T0 prints for 75s, then a switch to T1 which prints for 25s.
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(75));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true, Base.AddSeconds(75));
        accumulator.Sample(printerId, activeToolIndex: 1, isPrinting: true, Base.AddSeconds(100));

        IReadOnlyDictionary<int, double> drained = accumulator.DrainActiveSeconds(printerId);

        drained[0].Should().BeApproximately(75, 0.0001);
        drained[1].Should().BeApproximately(25, 0.0001);
    }

    [Fact]
    public void DrainActiveSeconds_ClearsBucketsButPreservesSegmentAcrossCycles()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(30));

        IReadOnlyDictionary<int, double> first = accumulator.DrainActiveSeconds(printerId);
        first[0].Should().BeApproximately(30, 0.0001);

        // The next sample continues from the drained boundary (Last* preserved), so the segment that
        // straddled the drain is credited into the following cycle rather than lost.
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(60));
        IReadOnlyDictionary<int, double> second = accumulator.DrainActiveSeconds(printerId);

        second[0].Should().BeApproximately(30, 0.0001);
    }

    [Fact]
    public void DrainActiveSeconds_AfterDrainWithNoNewSamples_ReturnsEmpty()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(30));
        accumulator.DrainActiveSeconds(printerId).Should().ContainKey(0);

        accumulator.DrainActiveSeconds(printerId).Should().BeEmpty();
    }

    [Fact]
    public void Sample_SegmentLongerThanCap_IsDroppedAsTelemetryGap()
    {
        // A segment longer than the freshness window is treated as a dropout (WebSocket gap, printer
        // paused off-camera) and credited to no tool: stale telemetry must never fabricate wear.
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromSeconds(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(3600));

        accumulator.DrainActiveSeconds(printerId).Should().BeEmpty();
    }

    [Fact]
    public void Sample_NonPrintingSegment_AccruesNothing()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: false, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: false, Base.AddSeconds(30));

        accumulator.DrainActiveSeconds(printerId).Should().BeEmpty();
    }

    [Fact]
    public void Sample_UnknownActiveTool_AccruesNothing()
    {
        // A printing segment with no known active tool (negative sentinel) must not be attributed.
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: -1, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: -1, isPrinting: true, Base.AddSeconds(30));

        accumulator.DrainActiveSeconds(printerId).Should().BeEmpty();
    }

    [Fact]
    public void Sample_OutOfOrderSample_IsIgnoredAndDoesNotRewindWindow()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(100));
        // Late arrival before the last accepted sample: ignored, must not credit or move the window.
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(50));
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(130));

        IReadOnlyDictionary<int, double> drained = accumulator.DrainActiveSeconds(printerId);

        // 100s (0→100) + 30s (100→130); the out-of-order 50 neither credited 50s nor rewound to
        // create an 80s (50→130) segment.
        drained[0].Should().BeApproximately(130, 0.0001);
    }

    [Fact]
    public void Reset_DiscardsAccumulatedActivity()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));
        Guid printerId = Guid.NewGuid();

        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base);
        accumulator.Sample(printerId, activeToolIndex: 0, isPrinting: true, Base.AddSeconds(30));

        accumulator.Reset(printerId);

        accumulator.DrainActiveSeconds(printerId).Should().BeEmpty();
    }

    [Fact]
    public void DrainActiveSeconds_UnknownPrinter_ReturnsEmpty()
    {
        var accumulator = new ToolheadActivityAccumulator(TimeSpan.FromMinutes(10));

        accumulator.DrainActiveSeconds(Guid.NewGuid()).Should().BeEmpty();
    }
}
