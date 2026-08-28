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
        double? bedTemp = 24.0) => new(
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
            SpoolInfo: null);

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
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };

        PrinterStatusBroadcastGate.ShouldBroadcast(lastSent, update).Should().BeTrue();
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
