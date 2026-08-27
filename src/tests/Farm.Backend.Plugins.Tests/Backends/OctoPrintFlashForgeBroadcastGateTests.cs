using Farm.Infrastructure;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Regression tests for issue #1497: confirms <see cref="PrinterStatusUpdate"/> record equality
/// is a true full-payload comparison for OctoPrint's and FlashForge's actual "printerupdated"
/// payload shapes, so <see cref="PrinterStatusBroadcastGate"/> correctly suppresses
/// byte-identical re-broadcasts for these two backends.
/// </summary>
/// <remarks>
/// Per <c>PrinterStatusBroadcastGate.cs:16-18</c>, record equality only holds while no populated
/// field is reference/array-typed (record equality compares arrays by reference). Neither
/// OctoPrint nor FlashForge populates <c>MmuStatus</c> (the only array-bearing field on
/// <see cref="PrinterStatusUpdate"/>, via <see cref="MmuStatusDto.Gates"/>) in their
/// "printerupdated" construction sites (<c>OctoPrintPollingService.cs</c>,
/// <c>OctoPrintWebSocketAdapter.cs</c>, <c>FlashForgePollingService.cs</c>), and
/// <c>ExtruderTemperatures</c>/<c>DetectedExtruderCount</c> only exist on the cache-only
/// <c>PrinterStatusDto</c>, not on <see cref="PrinterStatusUpdate"/>. These tests confirm that
/// hazard does not apply to these backends today, and would catch it if a future change added an
/// array-typed field to their payload construction.
/// </remarks>
public class OctoPrintFlashForgeBroadcastGateTests
{
    private static PrinterSpoolInfoDto MakeSpoolInfo() => new(
        HasActiveSpool: true,
        ActiveSpoolId: 7,
        SpoolName: "Black PLA",
        Material: "PLA",
        ColorHex: "#000000",
        FilamentName: "eSun PLA+",
        Vendor: "eSun",
        RemainingWeightG: 850.0,
        InitialWeightG: 1000.0,
        SpoolInUse: true);

    /// <summary>
    /// Mirrors the shape built at <c>OctoPrintPollingService.cs:447-464</c> /
    /// <c>OctoPrintWebSocketAdapter.cs:264-281</c> (no <c>MmuStatus</c>, populated
    /// <c>SpoolInfo</c>).
    /// </summary>
    private static PrinterStatusUpdate MakeOctoPrintUpdate(
        Guid printerId,
        string? state = "Operational",
        double? progress = null,
        double? hotendTemp = 25.0) => new(
            Id: printerId,
            IsOnline: true,
            State: state,
            Progress: progress,
            JobName: "benchy.gcode",
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            X: 0.0,
            Y: 0.0,
            Z: 0.0,
            HotendTemp: hotendTemp,
            BedTemp: 24.0,
            HotendTarget: 0.0,
            BedTarget: 0.0,
            HomedAxes: null,
            SpoolInfo: MakeSpoolInfo(),
            FileName: "benchy.gcode");

    /// <summary>
    /// Mirrors the shape built at <c>FlashForgePollingService.cs:284-301</c> (no
    /// <c>MmuStatus</c>, populated <c>SpoolInfo</c>).
    /// </summary>
    private static PrinterStatusUpdate MakeFlashForgeUpdate(
        Guid printerId,
        string? state = "Ready",
        double? progress = null,
        double? hotendTemp = 25.0) => new(
            Id: printerId,
            IsOnline: true,
            State: state,
            Progress: progress,
            JobName: "cube.gx",
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            X: 0.0,
            Y: 0.0,
            Z: 0.0,
            HotendTemp: hotendTemp,
            BedTemp: 24.0,
            HotendTarget: 0.0,
            BedTarget: 0.0,
            HomedAxes: null,
            SpoolInfo: MakeSpoolInfo(),
            FileName: "cube.gx");

    [Fact]
    public void PrinterStatusUpdate_Equals_StructurallyIdenticalOctoPrintPayloads_ReturnsTrue()
    {
        Guid printerId = Guid.NewGuid();
        PrinterStatusUpdate a = MakeOctoPrintUpdate(printerId);
        PrinterStatusUpdate b = MakeOctoPrintUpdate(printerId);

        a.Should().Be(b);
        PrinterStatusBroadcastGate.ShouldBroadcast(a, b).Should().BeFalse(
            "OctoPrint's payload shape (populated SpoolInfo, no MmuStatus) contains no " +
            "array-typed fields, so a repeat identical poll must be suppressed");
    }

    [Fact]
    public void ShouldBroadcast_OctoPrintPayloadFieldChanges_ReturnsTrue()
    {
        Guid printerId = Guid.NewGuid();
        PrinterStatusUpdate lastSent = MakeOctoPrintUpdate(printerId, progress: 10.0);
        PrinterStatusUpdate update = MakeOctoPrintUpdate(printerId, progress: 10.5);

        PrinterStatusBroadcastGate.ShouldBroadcast(lastSent, update).Should().BeTrue(
            "a genuine change must never be suppressed by the gate");
    }

    [Fact]
    public void PrinterStatusUpdate_Equals_StructurallyIdenticalFlashForgePayloads_ReturnsTrue()
    {
        Guid printerId = Guid.NewGuid();
        PrinterStatusUpdate a = MakeFlashForgeUpdate(printerId);
        PrinterStatusUpdate b = MakeFlashForgeUpdate(printerId);

        a.Should().Be(b);
        PrinterStatusBroadcastGate.ShouldBroadcast(a, b).Should().BeFalse(
            "FlashForge's payload shape (populated SpoolInfo, no MmuStatus) contains no " +
            "array-typed fields, so a repeat identical poll must be suppressed");
    }

    [Fact]
    public void ShouldBroadcast_FlashForgePayloadFieldChanges_ReturnsTrue()
    {
        Guid printerId = Guid.NewGuid();
        PrinterStatusUpdate lastSent = MakeFlashForgeUpdate(printerId, hotendTemp: 200.0);
        PrinterStatusUpdate update = MakeFlashForgeUpdate(printerId, hotendTemp: 205.0);

        PrinterStatusBroadcastGate.ShouldBroadcast(lastSent, update).Should().BeTrue(
            "a genuine change must never be suppressed by the gate");
    }
}
