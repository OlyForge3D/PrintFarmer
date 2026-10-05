using Farm.Infrastructure;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Printers.Tests.Services.SignalR;

/// <summary>
/// Unit tests for <see cref="PrinterStatusBroadcastGate"/> — the payload-complete equality gate
/// used by polling services to suppress byte-identical "printerupdated" re-broadcasts (issue #1355).
/// </summary>
public class PrinterStatusBroadcastGateTests
{
    private static PrinterStatusUpdate MakeUpdate(
        bool isOnline = true,
        string? state = "Idle",
        double? progress = null,
        string? jobName = null,
        double? hotendTemp = 25.0,
        double? bedTemp = 24.0,
        int? currentLayer = null,
        int? totalLayers = null,
        double? fanSpeedPercent = null,
        double? liveZOffsetMm = null) => new(
            Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            IsOnline: isOnline,
            State: state,
            Progress: progress,
            JobName: jobName,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            X: null,
            Y: null,
            Z: null,
            HotendTemp: hotendTemp,
            BedTemp: bedTemp,
            HotendTarget: null,
            BedTarget: null,
            HomedAxes: null,
            SpoolInfo: null,
            CurrentLayer: currentLayer,
            TotalLayers: totalLayers,
            FanSpeedPercent: fanSpeedPercent,
            LiveZOffsetMm: liveZOffsetMm);

    [Fact]
    public void ShouldBroadcast_WhenLastSentIsNull_ReturnsTrue()
    {
        // No prior cached value — e.g. first poll after backend restart, or first poll for a
        // newly-registered printer. Must never be suppressed.
        PrinterStatusBroadcastGate.ShouldBroadcast(lastSent: null, update: MakeUpdate())
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldBroadcast_WhenPayloadIsIdentical_ReturnsFalse()
    {
        PrinterStatusUpdate lastSent = MakeUpdate();
        PrinterStatusUpdate update = MakeUpdate();

        PrinterStatusBroadcastGate.ShouldBroadcast(lastSent, update).Should().BeFalse();
    }

    [Fact]
    public void ShouldBroadcast_WhenSameReference_ReturnsFalse()
    {
        PrinterStatusUpdate update = MakeUpdate();

        PrinterStatusBroadcastGate.ShouldBroadcast(update, update).Should().BeFalse();
    }

    [Theory]
    [InlineData("state")]
    [InlineData("progress")]
    [InlineData("jobName")]
    [InlineData("isOnline")]
    [InlineData("hotendTemp")]
    [InlineData("bedTemp")]
    [InlineData("currentLayer")]
    [InlineData("totalLayers")]
    [InlineData("fanSpeedPercent")]
    [InlineData("liveZOffsetMm")]
    public void ShouldBroadcast_WhenAnySingleFieldDiffers_ReturnsTrue(string changedField)
    {
        PrinterStatusUpdate lastSent = MakeUpdate();
        PrinterStatusUpdate update = changedField switch
        {
            "state" => MakeUpdate(state: "Printing"),
            "progress" => MakeUpdate(progress: 42.5),
            "jobName" => MakeUpdate(jobName: "benchy.gcode"),
            "isOnline" => MakeUpdate(isOnline: false),
            "hotendTemp" => MakeUpdate(hotendTemp: 210.0),
            "bedTemp" => MakeUpdate(bedTemp: 60.0),
            "currentLayer" => MakeUpdate(currentLayer: 3),
            "totalLayers" => MakeUpdate(totalLayers: 20),
            "fanSpeedPercent" => MakeUpdate(fanSpeedPercent: 65),
            "liveZOffsetMm" => MakeUpdate(liveZOffsetMm: 0.025),
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };

        PrinterStatusBroadcastGate.ShouldBroadcast(lastSent, update).Should().BeTrue();
    }

    [Fact]
    public void ToStatusDto_PreservesLayerCounters()
    {
        PrinterStatusDto status = MakeUpdate(currentLayer: 3, totalLayers: 20).ToStatusDto();

        status.CurrentLayer.Should().Be(3);
        status.TotalLayers.Should().Be(20);
    }

    [Fact]
    public void ToStatusDto_PreservesFanAndLiveZOffsetReadbacks()
    {
        PrinterStatusDto status = MakeUpdate(
            fanSpeedPercent: 65,
            liveZOffsetMm: 0.025).ToStatusDto();

        status.FanSpeedPercent.Should().Be(65);
        status.LiveZOffsetMm.Should().Be(0.025);
    }

    [Fact]
    public void ShouldBroadcast_OfflineThenRecovered_RecoveryIsNotSuppressed()
    {
        // Simulates the reconnect edge case: the last broadcast was an offline snapshot; the
        // recovery update differs (IsOnline true, real values) and must always be sent.
        PrinterStatusUpdate offline = MakeUpdate(isOnline: false, state: null, hotendTemp: null, bedTemp: null);
        PrinterStatusUpdate recovered = MakeUpdate(isOnline: true, state: "Idle", hotendTemp: 25.0, bedTemp: 24.0);

        PrinterStatusBroadcastGate.ShouldBroadcast(offline, recovered).Should().BeTrue();
    }
}
