using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Web.IntegrationTests.Calibration;

/// <summary>
/// Focused, no-network tests for <see cref="PinnedOrcaProfileCatalog"/>'s tuple selection against
/// hand-written <c>/api/profiles</c> JSON fixtures shaped like the real worker response
/// (<c>Farm.Slicer.Module.Dtos.AllProfilesResponseDto</c>): manufacturer-grouped arrays of
/// <c>{ name, manufacturer, compatible_printers, settings }</c> objects.
/// </summary>
/// <remarks>
/// These exercise exactly the bug the pinned smoke run hit: the catalogue must never hand out a
/// process or filament merely because it declares a functional field (<c>layer_height</c> /
/// <c>filament_type</c>); it must be explicitly compatible with the selected machine's exact name.
/// They also exercise the follow-up bug from the real smoke run (30197867094): a machine running
/// relative extrusion (<c>use_relative_e_distances=1</c>) depends on its own layer-change G-code to
/// reset E offsets, and the production plan compiler neutralizes that G-code, so the catalogue must
/// never select a relative-extrusion machine even when its nozzle diameter is otherwise the closest
/// match.
/// </remarks>
public sealed class PinnedOrcaProfileCatalogTests
{
    [Fact]
    public void Select_ChoosesExplicitlyCompatibleProcessAndFilament_ForNearestNozzleMachine()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "machineProfiles": {
                "Generic": [
                  {
                    "name": "Generic 0.4 nozzle",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.4"], "use_relative_e_distances": "0" }
                  },
                  {
                    "name": "Generic 0.6 nozzle",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.6"], "use_relative_e_distances": "0" }
                  }
                ]
              },
              "processProfiles": {
                "Generic": [
                  {
                    "name": "0.20mm Standard @0.4 nozzle",
                    "compatible_printers": ["Generic 0.4 nozzle"],
                    "settings": { "layer_height": "0.2" }
                  },
                  {
                    "name": "0.28mm Draft @0.6 nozzle",
                    "compatible_printers": ["Generic 0.6 nozzle"],
                    "settings": { "layer_height": "0.28" }
                  },
                  {
                    "name": "Universal ambiguous process",
                    "compatible_printers": [],
                    "settings": { "layer_height": "0.99" }
                  }
                ]
              },
              "filamentProfiles": {
                "Generic": [
                  {
                    "name": "Generic PLA @0.4 nozzle",
                    "manufacturer": "Generic",
                    "compatible_printers": ["Generic 0.4 nozzle"],
                    "settings": { "filament_type": "PLA" }
                  },
                  {
                    "name": "Generic PETG @0.6 nozzle",
                    "manufacturer": "Generic",
                    "compatible_printers": ["Generic 0.6 nozzle"],
                    "settings": { "filament_type": "PETG" }
                  },
                  {
                    "name": "Universal ambiguous filament",
                    "manufacturer": "OrcaFilamentLibrary",
                    "compatible_printers": [],
                    "settings": { "filament_type": "ABS" }
                  }
                ]
              }
            }
            """);

        PinnedOrcaProfileSelection selection = PinnedOrcaProfileCatalog.Select(document.RootElement);

        _ = selection.NozzleDiameterMillimeters.Should().Be(0.4);
        _ = selection.ProcessJson.Should().Contain("\"layer_height\":\"0.2\"");
        _ = selection.ProcessJson.Should().NotContain("0.28").And.NotContain("0.99");
        _ = selection.FilamentJson.Should().Contain("\"filament_type\":\"PLA\"");
        _ = selection.FilamentJson.Should().NotContain("PETG").And.NotContain("ABS");
    }

    [Fact]
    public void Select_PrefersSameManufacturerHierarchy_WhenMultipleFilamentsAreExplicitlyCompatible()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "machineProfiles": {
                "AwesomeCorp": [
                  {
                    "name": "AwesomePrinter 0.4 nozzle",
                    "manufacturer": "AwesomeCorp",
                    "settings": { "nozzle_diameter": ["0.4"], "use_relative_e_distances": "0" }
                  }
                ]
              },
              "processProfiles": {
                "Generic": [
                  {
                    "name": "0.20mm Standard @AwesomePrinter 0.4 nozzle",
                    "compatible_printers": ["AwesomePrinter 0.4 nozzle"],
                    "settings": { "layer_height": "0.2" }
                  }
                ]
              },
              "filamentProfiles": {
                "OrcaFilamentLibrary": [
                  {
                    "name": "Orca Generic PLA",
                    "manufacturer": "OrcaFilamentLibrary",
                    "compatible_printers": ["AwesomePrinter 0.4 nozzle"],
                    "settings": { "filament_type": "PLA", "cost": "1" }
                  }
                ],
                "AwesomeCorp": [
                  {
                    "name": "AwesomeCorp PLA",
                    "manufacturer": "AwesomeCorp",
                    "compatible_printers": ["AwesomePrinter 0.4 nozzle"],
                    "settings": { "filament_type": "PLA", "cost": "2" }
                  }
                ]
              }
            }
            """);

        PinnedOrcaProfileSelection selection = PinnedOrcaProfileCatalog.Select(document.RootElement);

        _ = selection.FilamentJson.Should().Contain("\"cost\":\"2\"", "the filament sharing the machine's manufacturer must win the tie-break");
    }

    [Fact]
    public void Select_Throws_WhenNoProcessIsExplicitlyCompatibleWithSelectedMachine()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "machineProfiles": {
                "Generic": [
                  {
                    "name": "Generic 0.4 nozzle",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.4"], "use_relative_e_distances": "0" }
                  }
                ]
              },
              "processProfiles": {
                "Generic": [
                  {
                    "name": "Universal ambiguous process",
                    "compatible_printers": [],
                    "settings": { "layer_height": "0.2" }
                  },
                  {
                    "name": "0.28mm Draft @0.6 nozzle",
                    "compatible_printers": ["Generic 0.6 nozzle"],
                    "settings": { "layer_height": "0.28" }
                  }
                ]
              },
              "filamentProfiles": {
                "Generic": [
                  {
                    "name": "Generic PLA @0.4 nozzle",
                    "manufacturer": "Generic",
                    "compatible_printers": ["Generic 0.4 nozzle"],
                    "settings": { "filament_type": "PLA" }
                  }
                ]
              }
            }
            """);

        Action select = () => PinnedOrcaProfileCatalog.Select(document.RootElement);

        _ = select.Should().Throw<InvalidOperationException>()
            .WithMessage("*no process profile explicitly compatible with machine 'Generic 0.4 nozzle'*");
    }

    [Fact]
    public void Select_Throws_WhenOnlyAUniversalFilamentIsPublished()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "machineProfiles": {
                "Generic": [
                  {
                    "name": "Generic 0.4 nozzle",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.4"], "use_relative_e_distances": "0" }
                  }
                ]
              },
              "processProfiles": {
                "Generic": [
                  {
                    "name": "0.20mm Standard @0.4 nozzle",
                    "compatible_printers": ["Generic 0.4 nozzle"],
                    "settings": { "layer_height": "0.2" }
                  }
                ]
              },
              "filamentProfiles": {
                "OrcaFilamentLibrary": [
                  {
                    "name": "Universal ambiguous filament",
                    "manufacturer": "OrcaFilamentLibrary",
                    "compatible_printers": [],
                    "settings": { "filament_type": "ABS" }
                  }
                ]
              }
            }
            """);

        Action select = () => PinnedOrcaProfileCatalog.Select(document.RootElement);

        _ = select.Should().Throw<InvalidOperationException>()
            .WithMessage("*no filament profile explicitly compatible with machine 'Generic 0.4 nozzle'*");
    }

    [Fact]
    public void Select_ExcludesRelativeExtrusionMachine_EvenWhenItsNozzleIsCloserToPreferred()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "machineProfiles": {
                "Generic": [
                  {
                    "name": "Generic 0.4 nozzle relative",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.4"], "use_relative_e_distances": "1" }
                  },
                  {
                    "name": "Generic 0.6 nozzle absolute",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.6"], "use_relative_e_distances": "0" }
                  }
                ]
              },
              "processProfiles": {
                "Generic": [
                  {
                    "name": "0.20mm Standard @0.4 nozzle relative",
                    "compatible_printers": ["Generic 0.4 nozzle relative"],
                    "settings": { "layer_height": "0.2" }
                  },
                  {
                    "name": "0.28mm Draft @0.6 nozzle absolute",
                    "compatible_printers": ["Generic 0.6 nozzle absolute"],
                    "settings": { "layer_height": "0.28" }
                  }
                ]
              },
              "filamentProfiles": {
                "Generic": [
                  {
                    "name": "Generic PLA @0.4 nozzle relative",
                    "manufacturer": "Generic",
                    "compatible_printers": ["Generic 0.4 nozzle relative"],
                    "settings": { "filament_type": "PLA" }
                  },
                  {
                    "name": "Generic PETG @0.6 nozzle absolute",
                    "manufacturer": "Generic",
                    "compatible_printers": ["Generic 0.6 nozzle absolute"],
                    "settings": { "filament_type": "PETG" }
                  }
                ]
              }
            }
            """);

        PinnedOrcaProfileSelection selection = PinnedOrcaProfileCatalog.Select(document.RootElement);

        _ = selection.NozzleDiameterMillimeters.Should().Be(
            0.6,
            "the 0.4mm machine runs relative extrusion and depends on its own layer-change G-code, " +
            "which the production plan compiler neutralizes, so only the absolute-extrusion machine is safe");
        _ = selection.MachineJson.Should().Contain("\"use_relative_e_distances\":\"0\"");
        _ = selection.ProcessJson.Should().Contain("\"layer_height\":\"0.28\"").And.NotContain("0.2\"");
        _ = selection.FilamentJson.Should().Contain("PETG").And.NotContain("PLA");
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("[\"1\"]")]
    public void Select_Throws_WhenEveryMachineDeclaresRelativeExtrusion(string useRelativeEDistancesJson)
    {
        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "machineProfiles": {
                "Generic": [
                  {
                    "name": "Generic 0.4 nozzle relative",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.4"], "use_relative_e_distances": {{useRelativeEDistancesJson}} }
                  }
                ]
              },
              "processProfiles": {
                "Generic": [
                  {
                    "name": "0.20mm Standard @0.4 nozzle relative",
                    "compatible_printers": ["Generic 0.4 nozzle relative"],
                    "settings": { "layer_height": "0.2" }
                  }
                ]
              },
              "filamentProfiles": {
                "Generic": [
                  {
                    "name": "Generic PLA @0.4 nozzle relative",
                    "manufacturer": "Generic",
                    "compatible_printers": ["Generic 0.4 nozzle relative"],
                    "settings": { "filament_type": "PLA" }
                  }
                ]
              }
            }
            """);

        Action select = () => PinnedOrcaProfileCatalog.Select(document.RootElement);

        _ = select.Should().Throw<InvalidOperationException>()
            .WithMessage("*no machine profile that declares both absolute extrusion*");
    }

    [Fact]
    public void Select_Throws_WhenNoMachineDeclaresUseRelativeEDistancesAtAll()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "machineProfiles": {
                "Generic": [
                  {
                    "name": "Generic 0.4 nozzle no extrusion mode",
                    "manufacturer": "Generic",
                    "settings": { "nozzle_diameter": ["0.4"] }
                  }
                ]
              },
              "processProfiles": {
                "Generic": [
                  {
                    "name": "0.20mm Standard @0.4 nozzle",
                    "compatible_printers": ["Generic 0.4 nozzle no extrusion mode"],
                    "settings": { "layer_height": "0.2" }
                  }
                ]
              },
              "filamentProfiles": {
                "Generic": [
                  {
                    "name": "Generic PLA @0.4 nozzle",
                    "manufacturer": "Generic",
                    "compatible_printers": ["Generic 0.4 nozzle no extrusion mode"],
                    "settings": { "filament_type": "PLA" }
                  }
                ]
              }
            }
            """);

        Action select = () => PinnedOrcaProfileCatalog.Select(document.RootElement);

        _ = select.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "*no machine profile that declares both absolute extrusion*",
                "an absent use_relative_e_distances key must never be treated as an implicit absolute-extrusion pass");
    }
}
