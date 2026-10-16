using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Issue #3323: ZMOD IFS (Flashforge AD5X) live per-gate telemetry from the <c>zmod_ifs</c> and
/// <c>zmod_color</c> Moonraker objects must populate the shared MMU state so AD5X printers expose
/// live gate presence, material, color, and active channel.
/// </summary>
public class ZmodIfsStatusParserTests
{
    private const string RigFrame =
        """
        {
          "zmod_ifs": {
            "available": true,
            "Ports": [true, false, false, true],
            "Chan": 4
          },
          "zmod_color": {
            "channel": 4,
            "slots": [
              { "ID": 1, "Material": "PLA", "Color": "Blue", "HEX": "A03CF7", "hasFilament": true },
              { "ID": 2, "Material": "?", "Color": "", "HEX": "", "hasFilament": false },
              { "ID": 3, "Material": "?", "Color": "", "HEX": "", "hasFilament": false },
              { "ID": 4, "Material": "PLA", "Color": "White", "HEX": "FFFFFF", "hasFilament": true }
            ]
          }
        }
        """;

    private static bool Frame(PrinterState state, string json, out int count)
    {
        Apply(state, json);
        count = 0;
        return state.TryGetStableZmodGateCount(
            ZmodIfsStatusParser.ReportedGateCount(JsonDocument.Parse(json).RootElement), out count);
    }

    private const string UnrelatedDelta = """{"zmod_ifs":{"Chan":2}}""";

    [Fact]
    public void TryGetStableZmodGateCount_FiresOnceAfterThreeReportingFrames()
    {
        var state = new PrinterState();

        Frame(state, RigFrame, out _).Should().BeFalse();
        Frame(state, RigFrame, out _).Should().BeFalse();
        Frame(state, RigFrame, out int count).Should().BeTrue();
        count.Should().Be(4);
        Frame(state, RigFrame, out _).Should().BeFalse();

        state.ResetMmuState();
        Frame(state, RigFrame, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetStableZmodGateCount_UnrelatedDeltasDoNotAdvanceDebounce()
    {
        var state = new PrinterState();

        Frame(state, RigFrame, out _).Should().BeFalse();
        for (int i = 0; i < 10; i++)
        {
            Frame(state, UnrelatedDelta, out _).Should().BeFalse();
        }

        Frame(state, RigFrame, out _).Should().BeFalse();
        Frame(state, RigFrame, out int count).Should().BeTrue();
        count.Should().Be(4);
    }

    [Fact]
    public void TryGetStableZmodGateCount_ChangingCountRestartsDebounce()
    {
        var state = new PrinterState();
        string sixPorts = """{"zmod_ifs":{"available":true,"Ports":[true,false,false,true,false,false],"Chan":1}}""";

        Frame(state, RigFrame, out _).Should().BeFalse();
        Frame(state, RigFrame, out _).Should().BeFalse();
        Frame(state, sixPorts, out _).Should().BeFalse();
        Frame(state, sixPorts, out _).Should().BeFalse();
        Frame(state, sixPorts, out int count).Should().BeTrue();
        count.Should().Be(6);
    }

    [Fact]
    public void TryGetStableZmodGateCount_NeverFiresWithoutLiveReport()
    {
        var state = new PrinterState();
        for (int i = 0; i < 10; i++)
        {
            state.TryGetStableZmodGateCount(0, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void TryGetStableZmodGateCount_ConflictingSlotCountDoesNotConfirmCachedPortCount()
    {
        var state = new PrinterState();
        string sixSlots = """{"zmod_color":{"channel":1,"slots":[{"ID":1},{"ID":2},{"ID":3},{"ID":4},{"ID":5},{"ID":6}]}}""";

        Frame(state, RigFrame, out _).Should().BeFalse();
        Frame(state, RigFrame, out _).Should().BeFalse();
        Frame(state, sixSlots, out _).Should().BeFalse();
        Frame(state, sixSlots, out _).Should().BeFalse();
        Frame(state, sixSlots, out int count).Should().BeTrue();
        count.Should().Be(6);
    }

    [Fact]
    public void Apply_RigFrame_PopulatesFourLiveGatesWithActiveChannel()
    {
        var state = new PrinterState();

        bool applied = Apply(state, RigFrame);

        applied.Should().BeTrue();
        state.MmuType.Should().Be(MmuProtocol.ZmodIfs);
        state.MmuDetected.Should().BeTrue();
        state.MmuNumGates.Should().Be(4);
        state.MmuGateStatus.Should().Equal(1, 0, 0, 1);
        state.MmuGateMaterial.Should().Equal("PLA", "", "", "PLA");
        state.MmuGateColor.Should().Equal("#A03CF7", "", "", "#FFFFFF");
        state.MmuActiveGate.Should().Be(3);
        state.MmuActiveTool.Should().Be(3);

        MmuStatusDto? status = state.BuildMmuStatus();
        status.Should().NotBeNull();
        status!.MmuType.Should().Be(MmuProtocol.ZmodIfs);
        status.ActiveGate.Should().Be(3);
        status.ActiveTool.Should().Be(3);
        status.Gates.Select(g => g.Name).Should().Equal("Gate 1", "Gate 2", "Gate 3", "Gate 4");
        status.Gates.Select(g => g.Status).Should().Equal(1, 0, 0, 1);
        status.Gates[3].Color.Should().Be("#FFFFFF");
    }

    // R3389-B01: ZMOD reports channel selection only (no head sensor / load-completion signal), so a
    // selected channel must never be published as a settled load that runout-switch logic trusts.
    [Fact]
    public void Apply_SelectedChannel_IsNeverReportedAsSettledLoad()
    {
        var state = new PrinterState();

        Apply(state, RigFrame);

        MmuStatusDto status = state.BuildMmuStatus()!;
        status.FilamentState.Should().Be("Unknown");
        new[] { "Loaded", "Ready" }.Should().NotContain(status.FilamentState);
        new[] { "Idle", "Printing" }.Should().NotContain(status.Action);
    }

    [Fact]
    public void Apply_SelectedChannelAfterOtherAction_DoesNotRetainSettledAction()
    {
        var state = new PrinterState { MmuAction = "Idle" };

        Apply(state, RigFrame);

        state.MmuAction.Should().BeNull();
    }

    // R3389-H01: malformed sensor values are unknown (status 2), not empty.
    [Fact]
    public void Apply_MalformedPortValues_ReportUnknownGateStatus()
    {
        var state = new PrinterState();

        Apply(state, """{ "zmod_ifs": { "Ports": [true, "yes", null, 7, false, 1, 0] } }""");

        state.ZmodIfsPorts.Should().Equal(true, null, null, null, false, true, false);
        state.MmuGateStatus.Should().Equal(1, 2, 2, 2, 0, 1, 0);
    }

    [Fact]
    public void Apply_MalformedSlotHasFilament_ReportsUnknownGateStatus()
    {
        var state = new PrinterState();

        Apply(
            state,
            """
            {
              "zmod_color": {
                "slots": [
                  { "ID": 1, "Material": "PLA", "HEX": "FFFFFF", "hasFilament": "maybe" },
                  { "ID": 2, "Material": "PLA", "HEX": "FFFFFF", "hasFilament": 1 }
                ]
              }
            }
            """);

        state.MmuGateStatus.Should().Equal(2, 1);
    }

    // R3389-H01: a malformed delta must not leave the previously cached presence in place.
    [Fact]
    public void Apply_MalformedSlotHasFilamentDelta_ClearsCachedPresenceToUnknown()
    {
        var state = new PrinterState();
        Apply(
            state,
            """
            { "zmod_color": { "slots": [ { "ID": 1, "Material": "PLA", "HEX": "FFFFFF", "hasFilament": true } ] } }
            """);
        state.MmuGateStatus.Should().Equal(1);

        Apply(state, """{ "zmod_color": { "slots": [ { "ID": 1, "hasFilament": "maybe" } ] } }""");

        state.MmuGateStatus.Should().Equal(2);
        state.MmuGateMaterial.Should().Equal("PLA");
    }

    // R3389-H01: an invalid nonzero channel is unknown, not an explicit "nothing loaded".
    [Theory]
    [InlineData("5")]
    [InlineData("-1")]
    [InlineData("\"two\"")]
    public void Apply_InvalidNonzeroChannel_ReportsUnknownWithNoActiveGate(string chan)
    {
        var state = new PrinterState();
        Apply(state, RigFrame);

        Apply(state, $$"""{ "zmod_ifs": { "Chan": {{chan}} }, "zmod_color": { "channel": {{chan}} } }""");

        state.MmuActiveGate.Should().Be(-1);
        state.MmuActiveTool.Should().Be(-1);
        state.MmuFilamentState.Should().Be("Unknown");
    }

    [Fact]
    public void Apply_PortsOnlyDiff_UpdatesPresenceAndPreservesSlotMaterials()
    {
        var state = new PrinterState();
        Apply(state, RigFrame);

        bool applied = Apply(state, """{ "zmod_ifs": { "Ports": [false, true, false, true] } }""");

        applied.Should().BeTrue();
        state.MmuGateStatus.Should().Equal(0, 1, 0, 1);
        state.MmuGateMaterial.Should().Equal("PLA", "", "", "PLA");
        state.MmuGateColor.Should().Equal("#A03CF7", "", "", "#FFFFFF");
        state.MmuActiveGate.Should().Be(3);
    }

    [Fact]
    public void Apply_ChannelZero_ReportsUnloadedWithNoActiveTool()
    {
        var state = new PrinterState();
        Apply(state, RigFrame);

        Apply(state, """{ "zmod_ifs": { "Chan": 0 }, "zmod_color": { "channel": 0 } }""");

        state.MmuActiveTool.Should().Be(-1);
        state.MmuActiveGate.Should().Be(-1);
        state.MmuFilamentState.Should().Be("Unloaded");
    }

    [Fact]
    public void Apply_ColorOnlyFrame_FallsBackToSlotHasFilamentForPresence()
    {
        var state = new PrinterState();

        Apply(
            state,
            """
            {
              "zmod_color": {
                "slots": [
                  { "ID": 1, "Material": "PETG", "HEX": "#00ff00", "hasFilament": true },
                  { "ID": 2, "Material": "?", "HEX": "", "hasFilament": false }
                ]
              }
            }
            """);

        state.MmuNumGates.Should().Be(2);
        state.MmuGateStatus.Should().Equal(1, 0);
        state.MmuGateColor.Should().Equal("#00FF00", "");
        state.MmuFilamentState.Should().Be("Unknown");
    }

    [Fact]
    public void Apply_IfsUnavailable_MarksMmuNotDetected()
    {
        var state = new PrinterState();

        Apply(state, """{ "zmod_ifs": { "available": false, "Ports": [false, false, false, false], "Chan": 0 } }""");

        state.ZmodIfsDetected.Should().BeTrue();
        state.MmuType.Should().Be(MmuProtocol.ZmodIfs);
        state.MmuDetected.Should().BeFalse();
        state.BuildMmuStatus().Should().BeNull();
    }

    [Fact]
    public void Apply_FrameWithoutZmodObjects_IsNoOp()
    {
        var state = new PrinterState();

        bool applied = Apply(state, """{ "print_stats": { "state": "standby" } }""");

        applied.Should().BeFalse();
        state.ZmodIfsDetected.Should().BeFalse();
        state.MmuType.Should().Be(MmuProtocol.Unknown);
        state.MmuDirty.Should().BeFalse();
    }

    [Fact]
    public void Apply_DoesNotOverrideAnotherMmuProtocol()
    {
        var state = new PrinterState { MmuType = MmuProtocol.HappyHare, MmuNumGates = 8 };

        bool applied = Apply(state, RigFrame);

        applied.Should().BeFalse();
        state.MmuType.Should().Be(MmuProtocol.HappyHare);
        state.MmuNumGates.Should().Be(8);
        state.ZmodIfsDetected.Should().BeFalse();
        state.MmuDirty.Should().BeFalse();
    }

    [Fact]
    public void Apply_OversizedPayload_IsCappedAtMaxGateCount()
    {
        var state = new PrinterState();
        string ports = string.Join(", ", Enumerable.Repeat("true", 64));

        Apply(state, $$"""{ "zmod_ifs": { "Ports": [{{ports}}] } }""");

        state.MmuNumGates.Should().Be(ZmodIfsStatusParser.MaxGateCount);
        state.MmuGateStatus.Should().HaveCount(ZmodIfsStatusParser.MaxGateCount);
    }

    [Fact]
    public void ResetMmuState_ClearsZmodTelemetry()
    {
        var state = new PrinterState();
        Apply(state, RigFrame);

        state.ResetMmuState();

        state.ZmodIfsDetected.Should().BeFalse();
        state.ZmodIfsAvailable.Should().BeNull();
        state.ZmodIfsPorts.Should().BeNull();
        state.ZmodIfsChannel.Should().BeNull();
        state.ZmodColorChannel.Should().BeNull();
        state.ZmodSlotCount.Should().Be(0);
        state.ZmodSlotHasFilament.Should().BeNull();
        state.MmuType.Should().Be(MmuProtocol.Unknown);
        state.MmuDetected.Should().BeFalse();
    }

    private static bool Apply(PrinterState state, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return ZmodIfsStatusParser.Apply(state, doc.RootElement);
    }
}
