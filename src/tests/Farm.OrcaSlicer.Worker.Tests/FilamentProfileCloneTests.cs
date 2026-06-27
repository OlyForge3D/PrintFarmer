using Farm.Slicer.Module.Dtos;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Guards the cache-safety of per-slice filament colour injection. Filament
/// profiles are resolved from a shared cache, so the worker clones a profile
/// before writing a per-slice <c>filament_colour</c> override into its Settings.
/// </summary>
public class FilamentProfileCloneTests
{
    [Fact]
    public void Clone_MutatingCloneSettings_DoesNotAffectOriginal()
    {
        var original = new FilamentProfileDto
        {
            Name = "Generic PLA",
            Color = "#000000",
            Settings = { ["filament_colour"] = "#000000", ["nozzle_temperature"] = "210" },
            CompatiblePrinters = { "RatRig V-Core 4 HYBRID 400 0.4 nozzle" },
        };

        FilamentProfileDto clone = original.Clone();
        clone.Color = "#FF8000";
        clone.Settings["filament_colour"] = "#FF8000";
        clone.Settings["new_key"] = "x";
        clone.CompatiblePrinters.Add("Another Printer");

        // Original is untouched — no cache pollution.
        original.Color.Should().Be("#000000");
        original.Settings["filament_colour"].Should().Be("#000000");
        original.Settings.Should().NotContainKey("new_key");
        original.CompatiblePrinters.Should().HaveCount(1);

        // Clone carries the override.
        clone.Settings["filament_colour"].Should().Be("#FF8000");
    }
}
