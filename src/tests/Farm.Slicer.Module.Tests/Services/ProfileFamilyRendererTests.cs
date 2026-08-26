using System.Text.Json;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Verifies that custom family rendering preserves source-preset semantics.
/// </summary>
public sealed class ProfileFamilyRendererTests
{
    private const string SourceManufacturer = "Prusa";
    private const string SourceModel = "Prusa Test";
    private const string FamilyName = "Farm Test";
    private const string PointFourMachine = "Prusa Test 0.4 nozzle";
    private const string PointSixMachine = "Prusa Test 0.6 nozzle";

    [Fact]
    public void Render_PointSixVariant_PreservesItsOwnLayerHeightDelta()
    {
        ProfileFamilyRenderResult result = Render(CreateCatalog());

        JsonElement pointSix = FindDocument(result, $"{FamilyName} 0.6 nozzle");

        pointSix.GetProperty("max_layer_height")[0].GetString().Should().Be("0.45");
        pointSix.GetProperty("max_layer_height")[0].GetString().Should().NotBe("0.32");
        pointSix.GetProperty("inherits").GetString().Should().Be($"{FamilyName} base");
    }

    [Fact]
    public void Render_ResolvedConditionSource_EmitsExactArrayAndClearsCondition()
    {
        AllProfilesResponseDto catalog = CreateCatalog();
        ProcessProfileDto conditionSource =
            catalog.ByHierarchy[SourceManufacturer].Models[SourceModel].ProcessProfiles[1];
        conditionSource.CompatiblePrintersCondition =
            "printer_notes=~/.*PRINTER_MODEL_TEST.*/ and nozzle_diameter[0]==0.6";

        ProfileFamilyRenderResult result = Render(catalog);

        JsonElement process = FindDocument(result, $"0.30mm Draft @{FamilyName}");
        process.GetProperty("compatible_printers")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Should()
            .Equal($"{FamilyName} 0.6 nozzle");
        process.GetProperty("compatible_printers_condition").GetString().Should().BeEmpty();
    }

    [Fact]
    public void Render_EmptyAndUniversalFilaments_EmitNoFilamentStubs()
    {
        AllProfilesResponseDto catalog = CreateCatalog();
        ProfileFamilyRenderResult emptyResult = Render(catalog);
        emptyResult.FilamentProfileCount.Should().Be(0);
        using (JsonDocument emptyManifest = JsonDocument.Parse(emptyResult.Bundle.ManifestJson))
        {
            emptyManifest.RootElement.GetProperty("filament_list").GetArrayLength().Should().Be(0);
        }

        catalog.ByHierarchy[SourceManufacturer].Models[SourceModel].FilamentProfiles =
        [
            new FilamentProfileDto
            {
                Name = "Generic PLA",
                Manufacturer = SourceManufacturer,
                CompatiblePrinters = []
            },
            new FilamentProfileDto
            {
                Name = "Orca PLA",
                Manufacturer = "OrcaFilamentLibrary",
                CompatiblePrinters = [PointFourMachine]
            }
        ];

        ProfileFamilyRenderResult result = Render(catalog);

        result.FilamentProfileCount.Should().Be(0);
        using JsonDocument manifest = JsonDocument.Parse(result.Bundle.ManifestJson);
        manifest.RootElement.GetProperty("filament_list").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Render_MissingRequestedSourceVariant_ThrowsRatherThanRenderingStrippedSettings()
    {
        AllProfilesResponseDto catalog = CreateCatalog(includePointSix: false);

        Action act = () => Render(catalog);

        act.Should()
            .Throw<ProfileFamilySourceException>()
            .WithMessage("*0.6 mm nozzle is unavailable*");
    }

    private static ProfileFamilyRenderResult Render(AllProfilesResponseDto catalog)
    {
        var request = new CloneProfileFamilyRequestDto
        {
            FamilyName = FamilyName,
            TargetPrinterModelId = Guid.NewGuid(),
            SourceManufacturer = SourceManufacturer,
            SourceMachineModelName = SourceModel,
            NozzleDiameters = [0.4, 0.6]
        };
        return new ProfileFamilyRenderer().Render(Guid.NewGuid(), request, catalog);
    }

    private static AllProfilesResponseDto CreateCatalog(bool includePointSix = true)
    {
        List<MachineProfileDto> machines =
        [
            Machine(
                PointFourMachine,
                0.4,
                "0.32",
                "0.20mm Standard @Prusa Test")
        ];
        if (includePointSix)
        {
            machines.Add(Machine(
                PointSixMachine,
                0.6,
                "0.45",
                "0.30mm Draft @Prusa Test"));
        }

        var model = new PrinterModelProfilesDto
        {
            Name = SourceModel,
            ModelId = "PRUSA_TEST",
            MachineProfiles = machines,
            ProcessProfiles =
            [
                new ProcessProfileDto
                {
                    Name = "0.20mm Standard @Prusa Test",
                    CompatiblePrinters = [PointFourMachine]
                },
                new ProcessProfileDto
                {
                    Name = "0.30mm Draft @Prusa Test",
                    CompatiblePrinters = [PointSixMachine]
                }
            ],
            FilamentProfiles = []
        };

        return new AllProfilesResponseDto
        {
            ByHierarchy = new Dictionary<string, ManufacturerProfilesDto>
            {
                [SourceManufacturer] = new ManufacturerProfilesDto
                {
                    Name = SourceManufacturer,
                    Models = new Dictionary<string, PrinterModelProfilesDto>
                    {
                        [SourceModel] = model
                    }
                }
            },
            MachineModelProfiles = new Dictionary<string, IList<MachineModelProfileDto>>
            {
                [SourceManufacturer] =
                [
                    new MachineModelProfileDto
                    {
                        Name = SourceModel,
                        Manufacturer = SourceManufacturer,
                        Settings = new Dictionary<string, object>
                        {
                            ["bed_model"] = "prusa_test.stl"
                        }
                    }
                ]
            }
        };
    }

    private static MachineProfileDto Machine(
        string name,
        double nozzle,
        string maxLayerHeight,
        string defaultPrintProfile) =>
        new()
        {
            Name = name,
            Manufacturer = SourceManufacturer,
            PrinterModel = SourceModel,
            NozzleDiameter = nozzle,
            Settings = new Dictionary<string, object>
            {
                ["name"] = name,
                ["printer_model"] = SourceModel,
                ["nozzle_diameter"] = new List<string> { nozzle.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) },
                ["min_layer_height"] = new List<string> { "0.08" },
                ["max_layer_height"] = new List<string> { maxLayerHeight },
                ["default_print_profile"] = defaultPrintProfile
            }
        };

    private static JsonElement FindDocument(ProfileFamilyRenderResult result, string name)
    {
        foreach (RenderedProfileFileDto file in result.Bundle.Files)
        {
            using JsonDocument document = JsonDocument.Parse(file.Content);
            if (document.RootElement.GetProperty("name").GetString() == name)
            {
                return document.RootElement.Clone();
            }
        }

        throw new InvalidOperationException($"Rendered document '{name}' was not found.");
    }
}
