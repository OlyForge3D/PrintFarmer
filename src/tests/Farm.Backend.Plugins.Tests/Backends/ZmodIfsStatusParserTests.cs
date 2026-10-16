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
        state.MmuFilamentState.Should().Be("Loaded");

        MmuStatusDto? status = state.BuildMmuStatus();
        status.Should().NotBeNull();
        status!.MmuType.Should().Be(MmuProtocol.ZmodIfs);
        status.Gates.Select(g => g.Name).Should().Equal("Gate 1", "Gate 2", "Gate 3", "Gate 4");
        status.Gates.Select(g => g.Status).Should().Equal(1, 0, 0, 1);
        status.Gates[3].Color.Should().Be("#FFFFFF");
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
        state.MmuActiveGate.Should().Be(0);
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
