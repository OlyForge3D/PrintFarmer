using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

public class SnapmakerU1PrintTaskConfigParserTests
{
    [Fact]
    public void TryParseDelta_PopulatedLanes_ReturnsPerToolheadMaterialColorAndActiveTool()
    {
        PrinterState state = Apply(
            new PrinterState(),
            """
            {
              "toolhead": { "extruder": "extruder2" },
              "print_task_config": {
                "filament_exist": [true, true, true, true],
                "filament_color_rgba": ["#ff0000ff", "00ff00ff", "#0000FFFF", "#123456"],
                "filament_type": ["PLA", "PETG", "ASA", "TPU"],
                "filament_sub_type": ["Matte", "NONE", "CF", ""],
                "filament_official": [true, false, true, false]
              }
            }
            """,
            allowToolheadOnly: false);

        state.SnapmakerU1ActiveTool.Should().Be(2);
        state.SnapmakerU1Lanes.Should().HaveCount(4);
        state.SnapmakerU1Lanes[0].Should().BeEquivalentTo(new
        {
            Index = 0,
            Loaded = true,
            Material = "PLA",
            SubType = "Matte",
            Color = "#FF0000",
            Official = true,
            IsActive = false,
            FilamentName = "PLA Matte"
        });
        state.SnapmakerU1Lanes[1].Material.Should().Be("PETG");
        state.SnapmakerU1Lanes[1].SubType.Should().BeNull();
        state.SnapmakerU1Lanes[1].Color.Should().Be("#00FF00");
        state.SnapmakerU1Lanes[2].IsActive.Should().BeTrue();
        state.SnapmakerU1Lanes[2].FilamentName.Should().Be("ASA CF");
    }

    [Fact]
    public void TryParseDelta_EmptyLane_ClearsMaterialAndColor()
    {
        PrinterState state = Apply(
            new PrinterState(),
            """
            {
              "toolhead": { "extruder": "extruder" },
              "print_task_config": {
                "filament_exist": [true, false, true, false],
                "filament_color_rgba": ["#ff0000ff", "#00ff00ff", "#0000ffff", "#ffffff"],
                "filament_type": ["PLA", "PETG", "ASA", "TPU"],
                "filament_sub_type": ["NONE", "NONE", "CF", "Flexible"],
                "filament_official": [false, true, false, true]
              }
            }
            """,
            allowToolheadOnly: false);

        state.SnapmakerU1ActiveTool.Should().Be(0);
        state.SnapmakerU1Lanes[1].Loaded.Should().BeFalse();
        state.SnapmakerU1Lanes[1].Material.Should().BeNull();
        state.SnapmakerU1Lanes[1].SubType.Should().BeNull();
        state.SnapmakerU1Lanes[1].Color.Should().BeNull();
        state.SnapmakerU1Lanes[1].Official.Should().BeFalse();
    }

    [Fact]
    public void TryParseDelta_PartialDeltas_MergeWithoutWipingAbsentFields()
    {
        PrinterState state = Apply(
            new PrinterState(),
            """
            {
              "toolhead": { "extruder": "extruder" },
              "print_task_config": {
                "filament_exist": [true, true, false, false],
                "filament_color_rgba": ["#ff0000ff", "#00ff00ff", "", ""],
                "filament_type": ["PLA", "PETG", "", ""],
                "filament_sub_type": ["Matte", "NONE", "", ""],
                "filament_official": [true, false, false, false]
              }
            }
            """,
            allowToolheadOnly: false);

        Apply(
            state,
            """{ "print_task_config": { "filament_exist": [true, true, false, false] } }""",
            allowToolheadOnly: true,
            expectedStateChange: false);

        state.SnapmakerU1Lanes[0].Material.Should().Be("PLA");
        state.SnapmakerU1Lanes[0].SubType.Should().Be("Matte");
        state.SnapmakerU1Lanes[0].Color.Should().Be("#FF0000");

        Apply(
            state,
            """{ "print_task_config": { "filament_color_rgba": ["#112233ff", "#00ff00ff", "", ""] } }""",
            allowToolheadOnly: true);

        state.SnapmakerU1Lanes[0].Loaded.Should().BeTrue();
        state.SnapmakerU1Lanes[0].Material.Should().Be("PLA");
        state.SnapmakerU1Lanes[0].Color.Should().Be("#112233");

        Apply(state, """{ "toolhead": { "extruder": "extruder2" } }""", allowToolheadOnly: true);

        state.SnapmakerU1ActiveTool.Should().Be(2);
        state.SnapmakerU1Lanes[2].IsActive.Should().BeTrue();

        Apply(
            state,
            """{ "print_task_config": { "filament_exist": [false, true, false, false] } }""",
            allowToolheadOnly: true);

        state.SnapmakerU1Lanes[0].Loaded.Should().BeFalse();
        state.SnapmakerU1Lanes[0].Material.Should().BeNull();
        state.SnapmakerU1Lanes[0].SubType.Should().BeNull();
        state.SnapmakerU1Lanes[0].Color.Should().BeNull();

        using JsonDocument unrelated = JsonDocument.Parse("""{ "display_status": { "progress": 0.5 } }""");
        bool parsed = SnapmakerU1PrintTaskConfigParser.TryParseDelta(
            unrelated.RootElement,
            allowToolheadOnly: true,
            out SnapmakerU1PrintTaskConfigDelta _);

        parsed.Should().BeFalse();
        state.SnapmakerU1Lanes[1].Material.Should().Be("PETG");
        state.SnapmakerU1Lanes[1].Color.Should().Be("#00FF00");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "print_task_config": null }""")]
    [InlineData("""{ "print_task_config": { "filament_exist": "bad" } }""")]
    [InlineData("""{ "print_task_config": { "unknown": ["PLA"] } }""")]
    public void TryParseDelta_MalformedOrMissingConfig_ReturnsFalseWithoutFalseState(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        bool parsed = SnapmakerU1PrintTaskConfigParser.TryParseDelta(
            doc.RootElement,
            allowToolheadOnly: false,
            out SnapmakerU1PrintTaskConfigDelta delta);

        parsed.Should().BeFalse();
        delta.Lanes.Should().BeEmpty();
        delta.ActiveTool.Should().BeNull();
        delta.HasLaneFields.Should().BeFalse();
    }

    [Fact]
    public void TryParseDelta_TypeOnlyDelta_AppliesToMergedState()
    {
        PrinterState state = Apply(
            new PrinterState(),
            """{ "print_task_config": { "filament_exist": [true, false, false, false] } }""",
            allowToolheadOnly: false);

        Apply(
            state,
            """{ "print_task_config": { "filament_type": ["ABS"] } }""",
            allowToolheadOnly: true);

        state.SnapmakerU1Lanes[0].Loaded.Should().BeTrue();
        state.SnapmakerU1Lanes[0].Material.Should().Be("ABS");
    }

    [Fact]
    public void BuildMmuStatus_ExistingProtocols_PreservesGateShape()
    {
        var happyHare = new PrinterState
        {
            MmuDetected = true,
            MmuEnabled = true,
            MmuType = MmuProtocol.HappyHare,
            MmuNumGates = 1,
            MmuGateStatus = [1],
            MmuGateMaterial = ["PLA"],
            MmuGateColor = ["#FF0000"],
            MmuGateFilamentName = ["PLA"],
            MmuGateSpoolId = [-1],
            MmuDirty = true
        };
        var qidibox = new PrinterState
        {
            MmuDetected = true,
            MmuEnabled = true,
            MmuType = MmuProtocol.Qidibox,
            MmuNumGates = 1,
            MmuGateStatus = [1],
            MmuGateMaterial = ["PETG"],
            MmuGateColor = ["#00FF00"],
            MmuGateFilamentName = ["PETG"],
            MmuGateSpoolId = [-1],
            MmuDirty = true
        };
        var afc = new PrinterState
        {
            MmuDetected = true,
            MmuEnabled = true,
            MmuType = MmuProtocol.Afc,
            MmuNumGates = 1,
            MmuGateStatus = [1],
            MmuGateMaterial = ["ASA"],
            MmuGateColor = ["#0000FF"],
            MmuGateFilamentName = ["ASA"],
            MmuGateSpoolId = [-1],
            AfcLaneNames = ["lane1"],
            MmuDirty = true
        };

        happyHare.BuildMmuStatus()!.Gates[0].Name.Should().BeNull();
        qidibox.BuildMmuStatus()!.Gates[0].Name.Should().Be("slot0");
        afc.BuildMmuStatus()!.Gates[0].Name.Should().Be("lane1");
    }

    [Fact]
    public void BuildMmuStatus_SnapmakerU1_LabelsHardwareInferredUiLanes()
    {
        var state = new PrinterState
        {
            MmuDetected = true,
            MmuEnabled = true,
            MmuType = MmuProtocol.SnapmakerU1,
            MmuNumGates = 4,
            MmuGateStatus = [1, 0, 1, 0],
            MmuGateMaterial = ["PLA", "", "ASA", ""],
            MmuGateColor = ["#FF0000", "", "#0000FF", ""],
            MmuGateFilamentName = ["PLA", "", "ASA", ""],
            MmuGateSpoolId = [-1, -1, -1, -1],
            MmuDirty = true
        };

        MmuStatusDto status = state.BuildMmuStatus()!;

        status.MmuType.Should().Be(MmuProtocol.SnapmakerU1);
        status.Gates.Select(g => g.Name).Should().Equal("T1", "T2", "T3", "T4");
    }

    private static PrinterState Apply(
        PrinterState state,
        string json,
        bool allowToolheadOnly,
        bool expectedStateChange = true)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        bool parsed = SnapmakerU1PrintTaskConfigParser.TryParseDelta(
            doc.RootElement,
            allowToolheadOnly,
            out SnapmakerU1PrintTaskConfigDelta delta);

        parsed.Should().BeTrue();
        state.MergeSnapmakerU1Delta(delta).Should().Be(expectedStateChange);
        return state;
    }
}
