using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Integration tests for OrcaSlicer bundle preview endpoint.
/// </summary>
public class OrcaBundlePreviewTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrcaBundlePreviewTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "Preview valid Orca bundle returns structured preview")]
    public async Task Preview_ValidOrcaBundle_ReturnsPreview()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Sample minimal Orca bundle with one printer, one filament, one process
        var bundleJson = """
        {
            "printer": [
                {
                    "name": "Test Printer",
                    "printer_model": "Generic FDM",
                    "printer_vendor": "Test Manufacturer",
                    "bed_width": 220,
                    "bed_depth": 220,
                    "max_print_height": 250,
                    "nozzle_diameter": 0.4,
                    "max_bed_temperature": 100,
                    "max_hotend_temperature": 300
                }
            ],
            "filament": [
                {
                    "name": "Generic PLA",
                    "filament_type": "PLA",
                    "nozzle_temperature": 210,
                    "bed_temperature": 60
                }
            ],
            "process": [
                {
                    "name": "Standard Quality",
                    "layer_height": 0.2,
                    "fill_density": 20,
                    "print_speed": 50
                }
            ]
        }
        """;

        var request = new ImportOrcaBundleDto
        {
            BundleJson = bundleJson
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseBody = await response.Content.ReadAsStringAsync();
        var preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(responseBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        preview.Should().NotBeNull();
        preview!.Printers.Should().HaveCount(1);
        preview.Printers[0].Name.Should().Be("Test Printer");
        preview.Printers[0].PrinterModel.Should().Be("Generic FDM");
        preview.Printers[0].NozzleDiameter.Should().Be(0.4);

        preview.Filaments.Should().HaveCount(1);
        preview.Filaments[0].Name.Should().Be("Generic PLA");
        preview.Filaments[0].FilamentType.Should().Be("PLA");

        preview.Processes.Should().HaveCount(1);
        preview.Processes[0].Name.Should().Be("Standard Quality");
        preview.Processes[0].LayerHeight.Should().Be(0.2);
    }

    [Fact(DisplayName = "Preview with invalid bundle format returns 400")]
    public async Task Preview_InvalidBundleFormat_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();

        var request = new ImportOrcaBundleDto
        {
            BundleJson = "{ \"invalid\": \"structure\" }" // Missing printer/filament/process sections
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Preview with empty bundle JSON returns 400")]
    public async Task Preview_EmptyBundleJson_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();

        var request = new ImportOrcaBundleDto
        {
            BundleJson = ""
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Preview handles multiple presets per section")]
    public async Task Preview_MultiplePresetsPerSection_ParsesAll()
    {
        // Arrange
        var client = _factory.CreateClient();

        var bundleJson = """
        {
            "printer": [
                { "name": "Printer A", "nozzle_diameter": 0.4 },
                { "name": "Printer B", "nozzle_diameter": 0.6 }
            ],
            "filament": [
                { "name": "PLA Red", "filament_type": "PLA" },
                { "name": "PETG Blue", "filament_type": "PETG" },
                { "name": "ABS Black", "filament_type": "ABS" }
            ],
            "process": [
                { "name": "Draft", "layer_height": 0.3 },
                { "name": "Standard", "layer_height": 0.2 },
                { "name": "Fine", "layer_height": 0.1 }
            ]
        }
        """;

        var request = new ImportOrcaBundleDto { BundleJson = bundleJson };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        var preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(responseBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        preview.Should().NotBeNull();
        preview!.Printers.Should().HaveCount(2);
        preview.Filaments.Should().HaveCount(3);
        preview.Processes.Should().HaveCount(3);
    }
}
