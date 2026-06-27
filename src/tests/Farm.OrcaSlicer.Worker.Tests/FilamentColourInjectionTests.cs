using System.Text.Json;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Directly exercises HttpJobPollerService.ApplyFilamentColourOverrides — the
/// parsing/injection of per-slice filament_colour overrides from slicerProfileJson.
/// Guards against silent no-ops (wrong JSON keys) and cache pollution.
/// </summary>
public class FilamentColourInjectionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static FilamentProfileDto Filament(string name) => new()
    {
        Name = name,
        Settings = { ["filament_colour"] = new List<string> { "#000000" } },
    };

    [Fact]
    public void Multi_InjectsPositionalColoursIntoEachExtruder()
    {
        var profile = new SlicerProfileDto
        {
            ExtruderFilamentProfiles = [Filament("PLA A"), Filament("PLA B")],
        };

        HttpJobPollerService.ApplyFilamentColourOverrides(
            profile, Parse("""{ "filamentColours": ["#FF0000", "#00FF00"] }"""));

        profile.ExtruderFilamentProfiles![0].Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#FF0000");
        profile.ExtruderFilamentProfiles![1].Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#00FF00");
        profile.ExtruderFilamentProfiles![0].Color.Should().Be("#FF0000");
    }

    [Fact]
    public void Single_InjectsColourIntoPrimaryFilament()
    {
        var profile = new SlicerProfileDto { FilamentProfile = Filament("PLA") };

        HttpJobPollerService.ApplyFilamentColourOverrides(
            profile, Parse("""{ "filamentColour": "#0000FF" }"""));

        profile.FilamentProfile!.Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#0000FF");
        profile.FilamentProfile!.Color.Should().Be("#0000FF");
    }

    [Fact]
    public void Multi_MoreColoursThanExtruders_DoesNotThrow_AndOnlyUpdatesExisting()
    {
        var profile = new SlicerProfileDto { ExtruderFilamentProfiles = [Filament("PLA A")] };

        Action act = () => HttpJobPollerService.ApplyFilamentColourOverrides(
            profile, Parse("""{ "filamentColours": ["#FF0000", "#00FF00", "#0000FF"] }"""));

        act.Should().NotThrow();
        profile.ExtruderFilamentProfiles!.Should().HaveCount(1);
        profile.ExtruderFilamentProfiles![0].Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#FF0000");
    }

    [Fact]
    public void NullOrEmptyColour_IsSkipped_LeavingExistingValue()
    {
        var profile = new SlicerProfileDto
        {
            ExtruderFilamentProfiles = [Filament("PLA A"), Filament("PLA B")],
        };

        HttpJobPollerService.ApplyFilamentColourOverrides(
            profile, Parse("""{ "filamentColours": ["", "#00FF00"] }"""));

        // First entry kept its original colour; second was overridden.
        profile.ExtruderFilamentProfiles![0].Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#000000");
        profile.ExtruderFilamentProfiles![1].Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#00FF00");
    }

    [Fact]
    public void NoColourKeys_LeavesProfileUnchanged()
    {
        var profile = new SlicerProfileDto { FilamentProfile = Filament("PLA") };

        HttpJobPollerService.ApplyFilamentColourOverrides(profile, Parse("""{ "machineProfileName": "X" }"""));

        profile.FilamentProfile!.Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#000000");
    }

    [Fact]
    public void SingleExtruderMulti_PrimaryAliasingExtruderZero_GetsColour()
    {
        // A multi-extruder job with exactly one extruder where FilamentProfile
        // aliases extruder[0] (as set via `??=` during resolution). The worker's
        // single-extruder pipeline branch reads FilamentProfile, so the override
        // must follow onto the cloned, coloured profile — not be dropped.
        FilamentProfileDto shared = Filament("PLA A");
        var profile = new SlicerProfileDto
        {
            ExtruderFilamentProfiles = [shared],
            FilamentProfile = shared,
        };

        HttpJobPollerService.ApplyFilamentColourOverrides(
            profile, Parse("""{ "filamentColours": ["#FF0000"] }"""));

        // FilamentProfile now points at the coloured clone (reference-synced).
        ReferenceEquals(profile.FilamentProfile, profile.ExtruderFilamentProfiles![0]).Should().BeTrue();
        profile.FilamentProfile!.Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#FF0000");
        // Original shared instance is untouched (cache safety).
        shared.Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#000000");
    }

    [Fact]
    public void Multi_PrimaryNotAliasingExtruderZero_IsNotRepointed()
    {
        // When FilamentProfile is a distinct profile (not extruder[0]), it must be
        // left alone — only the extruder list slots are coloured.
        FilamentProfileDto distinctPrimary = Filament("Primary");
        var profile = new SlicerProfileDto
        {
            ExtruderFilamentProfiles = [Filament("PLA A"), Filament("PLA B")],
            FilamentProfile = distinctPrimary,
        };

        HttpJobPollerService.ApplyFilamentColourOverrides(
            profile, Parse("""{ "filamentColours": ["#FF0000", "#00FF00"] }"""));

        ReferenceEquals(profile.FilamentProfile, distinctPrimary).Should().BeTrue();
        profile.FilamentProfile!.Settings["filament_colour"]
            .Should().BeOfType<List<string>>().Which.Should().ContainSingle().Which.Should().Be("#000000");
    }
}
