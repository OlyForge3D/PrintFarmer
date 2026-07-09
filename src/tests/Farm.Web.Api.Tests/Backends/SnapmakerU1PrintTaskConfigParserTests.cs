using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

public class SnapmakerU1PrintTaskConfigParserTests
{
    [Fact]
    public void TryParse_PopulatedLanes_ReturnsPerToolheadMaterialColorAndActiveTool()
    {
        using JsonDocument doc = JsonDocument.Parse(
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
            """);

        bool parsed = SnapmakerU1PrintTaskConfigParser.TryParse(doc.RootElement, out SnapmakerU1PrintTaskConfigStatus status);

        parsed.Should().BeTrue();
        status.ActiveTool.Should().Be(2);
        status.Lanes.Should().HaveCount(4);
        status.Lanes[0].Should().BeEquivalentTo(new
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
        status.Lanes[1].Material.Should().Be("PETG");
        status.Lanes[1].SubType.Should().BeNull();
        status.Lanes[1].Color.Should().Be("#00FF00");
        status.Lanes[2].IsActive.Should().BeTrue();
        status.Lanes[2].FilamentName.Should().Be("ASA CF");
    }

    [Fact]
    public void TryParse_EmptyLane_ReturnsEmptyMaterialAndColor()
    {
        using JsonDocument doc = JsonDocument.Parse(
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
            """);

        bool parsed = SnapmakerU1PrintTaskConfigParser.TryParse(doc.RootElement, out SnapmakerU1PrintTaskConfigStatus status);

        parsed.Should().BeTrue();
        status.ActiveTool.Should().Be(0);
        status.Lanes[1].Loaded.Should().BeFalse();
        status.Lanes[1].Material.Should().BeNull();
        status.Lanes[1].SubType.Should().BeNull();
        status.Lanes[1].Color.Should().BeNull();
        status.Lanes[1].Official.Should().BeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "print_task_config": null }""")]
    [InlineData("""{ "print_task_config": { "filament_type": ["PLA"] } }""")]
    [InlineData("""{ "print_task_config": { "filament_exist": "bad" } }""")]
    public void TryParse_MalformedOrMissingConfig_ReturnsFalseWithoutFalseState(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        bool parsed = SnapmakerU1PrintTaskConfigParser.TryParse(doc.RootElement, out SnapmakerU1PrintTaskConfigStatus status);

        parsed.Should().BeFalse();
        status.Lanes.Should().BeEmpty();
        status.ActiveTool.Should().Be(-2);
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
}
