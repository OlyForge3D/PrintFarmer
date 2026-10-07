using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Regression for #3292: Qidi firmware reports the external spool holder ("Rack") as
/// <c>filament_slot16</c> / <c>last_load_slot = "slot16"</c>. It must surface as the Happy Hare
/// bypass sentinel (-2) and never as an out-of-range MMU gate.
/// </summary>
public class QidiboxRackTelemetryTests
{
    // Mirrors qp4-1 save_variables (read-only capture): one box, slots 0..3, Rack slot16 = type 41.
    private static JsonElement Qp41Variables(string lastLoadSlot) => JsonDocument.Parse($$"""
        {
          "box_count": 1,
          "filament_slot0": 1, "color_slot0": 1,
          "filament_slot1": 2, "color_slot1": 2,
          "filament_slot2": 1, "color_slot2": 3,
          "filament_slot3": 3, "color_slot3": 4,
          "filament_slot16": 41, "color_slot16": 5,
          "last_load_slot": "{{lastLoadSlot}}"
        }
        """).RootElement;

    private static PrinterState NewQidiState() => new()
    {
        MmuDetected = true,
        MmuEnabled = true,
        QidiboxDetected = true,
        MmuType = MmuProtocol.Qidibox,
        QidiboxFilamentDict = new Dictionary<int, string> { [1] = "PLA", [2] = "PETG", [3] = "ABS", [41] = "ASA" },
        QidiboxColorDict = new Dictionary<int, string> { [1] = "#FF0000", [2] = "#00FF00", [3] = "#0000FF", [4] = "#FFFF00", [5] = "#FFFFFF" },
    };

    [Fact]
    public void Slot16_IsReportedAsBypass_NotAsGate()
    {
        PrinterState state = NewQidiState();

        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables("slot16"));
        MmuStatusDto mmu = state.BuildMmuStatus()!;

        mmu.NumGates.Should().Be(4);
        mmu.Gates.Select(g => g.Index).Should().Equal(0, 1, 2, 3);
        mmu.Gates.Select(g => g.Material).Should().Equal("PLA", "PETG", "PLA", "ABS");
        mmu.Gates.Should().NotContain(g => g.Material == "ASA", "the Rack is not a box gate");
        mmu.ActiveGate.Should().Be(MoonrakerSubscriptionService.MmuGateBypass);
        mmu.ActiveTool.Should().Be(MoonrakerSubscriptionService.MmuGateBypass);
        mmu.HasBypass.Should().BeTrue();
    }

    [Fact]
    public void ActiveFeed_TransitionsBetweenGateAndRack_AndInvalidatesCachedStatus()
    {
        PrinterState state = NewQidiState();

        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables("slot1"));
        MmuStatusDto onGate = state.BuildMmuStatus()!;
        onGate.ActiveGate.Should().Be(1);
        onGate.ActiveTool.Should().Be(1);
        onGate.HasBypass.Should().BeTrue("filament_slot16 proves the Rack exists");

        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables("slot16"));
        MmuStatusDto onRack = state.BuildMmuStatus()!;
        onRack.Should().NotBeSameAs(onGate, "a stale cached status would hide the switch to the Rack");
        onRack.ActiveGate.Should().Be(-2);
        onRack.Gates.Select(g => g.Material).Should().Equal(onGate.Gates.Select(g => g.Material));

        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables("slot3"));
        state.BuildMmuStatus()!.ActiveGate.Should().Be(3);

        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables("slot-1"));
        MmuStatusDto unloaded = state.BuildMmuStatus()!;
        unloaded.ActiveGate.Should().Be(-1);
        unloaded.ActiveTool.Should().Be(-1);
    }

    [Theory]
    [InlineData("slot4")]
    [InlineData("slot15")]
    [InlineData("slot17")]
    public void UnknownOutOfRangeSlot_NeverBecomesActiveGate(string lastLoadSlot)
    {
        PrinterState state = NewQidiState();

        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables(lastLoadSlot));
        MmuStatusDto mmu = state.BuildMmuStatus()!;

        mmu.ActiveGate.Should().Be(-1);
        mmu.ActiveTool.Should().Be(-1);
    }

    [Fact]
    public void UnchangedSaveVariables_KeepCachedStatus()
    {
        PrinterState state = NewQidiState();
        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables("slot16"));
        MmuStatusDto first = state.BuildMmuStatus()!;

        MoonrakerSubscriptionService.ApplyQidiboxSaveVariables(state, Qp41Variables("slot16"));

        state.BuildMmuStatus().Should().BeSameAs(first);
    }
}
