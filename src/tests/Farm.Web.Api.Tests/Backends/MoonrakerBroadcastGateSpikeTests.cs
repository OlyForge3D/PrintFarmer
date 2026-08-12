using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

/// <summary>
/// Spike tests for issue #1498: establishes whether <see cref="PrinterStatusUpdate"/> record
/// equality and <see cref="PrinterState.BuildMmuStatus"/> caching are value-correct enough for
/// <c>PrinterStatusBroadcastGate</c> (see docs/spike-1242-signalr-broadcast-volume.md) to be
/// wired into the Moonraker backend safely. These tests answer spike questions 2 and 3; they are
/// a measurement artifact, not production wiring — no gate is applied to Moonraker here.
/// </summary>
public class MoonrakerBroadcastGateSpikeTests
{
    /// <summary>
    /// Question 2: does <see cref="PrinterStatusUpdate"/>.Equals return true for two
    /// structurally-identical Moonraker payloads that include populated MMU data?
    /// </summary>
    /// <remarks>
    /// The two <see cref="PrinterState"/> instances are separate objects populated with identical
    /// MMU field values and each independently marked dirty, so <see cref="PrinterState.BuildMmuStatus"/>
    /// builds two distinct <c>MmuGateDto[]</c> array instances with equal contents. Record equality
    /// compares arrays by reference, not value, so this reproduces the exact hazard called out in
    /// <c>PrinterStatusBroadcastGate.cs:16-18</c> for any Moonraker printer with MMU/AFC/Qidibox
    /// hardware attached.
    /// </remarks>
    [Fact]
    public void PrinterStatusUpdate_Equals_StructurallyIdenticalMmuPayloads_ReturnsFalse()
    {
        MmuStatusDto mmuA = BuildHappyHareMmuStatus();
        MmuStatusDto mmuB = BuildHappyHareMmuStatus();

        // Sanity check: the gate arrays are content-equal but reference-distinct, which is the
        // precondition for the hazard under test.
        mmuA.Gates.Should().NotBeSameAs(mmuB.Gates);
        mmuA.Gates.Should().BeEquivalentTo(mmuB.Gates);

        Guid printerId = Guid.NewGuid();
        PrinterStatusUpdate updateA = BuildConsolidatedUpdate(printerId, mmuA);
        PrinterStatusUpdate updateB = BuildConsolidatedUpdate(printerId, mmuB);

        // Every scalar field is identical; only the MMU gate array identity differs. If record
        // equality were a true full-payload value comparison, these would be Equal. They are not,
        // because MmuStatusDto.Gates is an array and array equality is reference-based.
        updateA.Should().NotBe(updateB);
        updateA.Equals(updateB).Should().BeFalse(
            "PrinterStatusUpdate equality compares MmuStatus.Gates by array reference, not by value, " +
            "so PrinterStatusBroadcastGate would never suppress a repeat broadcast for any MMU-equipped " +
            "Moonraker printer even when nothing actually changed");
    }

    /// <summary>
    /// Question 3 (first half): does <see cref="PrinterState.BuildMmuStatus"/> return the same
    /// array/DTO instance across polls when MMU state has not changed?
    /// </summary>
    [Fact]
    public void BuildMmuStatus_NoStateChangeBetweenCalls_ReturnsSameCachedInstance()
    {
        PrinterState state = CreateHappyHareState(dirty: true);

        MmuStatusDto? first = state.BuildMmuStatus();

        // Simulate a second poll where nothing about the MMU changed: MmuDirty was cleared by the
        // first BuildMmuStatus call and nothing set it again.
        MmuStatusDto? second = state.BuildMmuStatus();

        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "BuildMmuStatus should reuse its cached DTO (and gate array) when MmuDirty is false");
    }

    /// <summary>
    /// Question 3 (second half): does <see cref="PrinterState.BuildMmuStatus"/> return a new
    /// instance once MMU state has actually changed?
    /// </summary>
    [Fact]
    public void BuildMmuStatus_StateChangesBetweenCalls_ReturnsNewInstance()
    {
        PrinterState state = CreateHappyHareState(dirty: true);
        MmuStatusDto? first = state.BuildMmuStatus();

        // Simulate a real MMU state change (gate 0 becomes empty) as HandleMmuUpdate would apply,
        // which must set MmuDirty = true for the next BuildMmuStatus call to rebuild.
        state.MmuGateStatus = [0];
        state.MmuDirty = true;

        MmuStatusDto? second = state.BuildMmuStatus();

        second.Should().NotBeNull();
        second.Should().NotBeSameAs(first, "a real MMU state change must produce a fresh DTO/array instance, not the stale cached one");
        second!.Gates[0].Status.Should().Be(0);
        first!.Gates[0].Status.Should().Be(1, "the original cached instance must not be mutated in place by the rebuild");
    }

    private static MmuStatusDto BuildHappyHareMmuStatus() =>
        CreateHappyHareState(dirty: true).BuildMmuStatus()!;

    private static PrinterState CreateHappyHareState(bool dirty) => new()
    {
        MmuDetected = true,
        MmuEnabled = true,
        MmuIsHomed = true,
        MmuType = MmuProtocol.HappyHare,
        MmuActiveTool = 0,
        MmuActiveGate = 0,
        MmuFilamentState = "Loaded",
        MmuAction = "Idle",
        MmuNumGates = 1,
        MmuHasBypass = false,
        MmuEndlessSpool = false,
        MmuClogDetection = true,
        MmuGateStatus = [1],
        MmuGateMaterial = ["PLA"],
        MmuGateColor = ["#FF0000"],
        MmuGateFilamentName = ["eSun PLA+"],
        MmuGateSpoolId = [42],
        MmuDirty = dirty,
    };

    /// <summary>
    /// Mirrors the shape built by <c>MoonrakerSubscriptionService.EmitConsolidatedStatusAsync</c>
    /// (a captured/representative Moonraker consolidated payload), holding every field constant
    /// except the supplied <see cref="MmuStatusDto"/>.
    /// </summary>
    private static PrinterStatusUpdate BuildConsolidatedUpdate(Guid printerId, MmuStatusDto mmuStatus) => new(
        printerId,
        IsOnline: true,
        State: "Printing",
        Progress: 42.5,
        JobName: "benchy.gcode",
        ThumbnailUrl: null,
        CameraStreamUrl: null,
        X: 100.0,
        Y: 100.0,
        Z: 12.4,
        HotendTemp: 215.0,
        BedTemp: 60.0,
        HotendTarget: 215.0,
        BedTarget: 60.0,
        HomedAxes: "xyz",
        SpoolInfo: null,
        MmuStatus: mmuStatus,
        FileName: "benchy.gcode");
}
