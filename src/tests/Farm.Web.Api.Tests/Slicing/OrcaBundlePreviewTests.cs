using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Integration tests for OrcaSlicer bundle preview endpoint.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OrcaBundlePreviewTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    public OrcaBundlePreviewTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact(DisplayName = "Preview valid Orca bundle returns structured preview")]
    public async Task Preview_ValidOrcaBundle_ReturnsPreview()
    {
        // Arrange
        HttpClient client = _client;

        // Sample minimal Orca bundle with one printer, one filament, one process
        string bundleJson = """
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

        ImportOrcaBundleDto request = new ImportOrcaBundleDto
        {
            BundleJson = bundleJson
        };

        StringContent content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(responseBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Printers.Should().HaveCount(1);
        _ = preview.Printers[0].Name.Should().Be("Test Printer");
        _ = preview.Printers[0].PrinterModel.Should().Be("Generic FDM");
        _ = preview.Printers[0].NozzleDiameter.Should().Be(0.4);

        _ = preview.Filaments.Should().HaveCount(1);
        _ = preview.Filaments[0].Name.Should().Be("Generic PLA");
        _ = preview.Filaments[0].FilamentType.Should().Be("PLA");

        _ = preview.Processes.Should().HaveCount(1);
        _ = preview.Processes[0].Name.Should().Be("Standard Quality");
        _ = preview.Processes[0].LayerHeight.Should().Be(0.2);
    }

    [Fact(DisplayName = "Preview with invalid bundle format returns 400")]
    public async Task Preview_InvalidBundleFormat_ReturnsBadRequest()
    {
        // Arrange
        HttpClient client = _client;

        ImportOrcaBundleDto request = new ImportOrcaBundleDto
        {
            BundleJson = "{ \"invalid\": \"structure\" }" // Missing printer/filament/process sections
        };

        StringContent content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Preview with empty bundle JSON returns 400")]
    public async Task Preview_EmptyBundleJson_ReturnsBadRequest()
    {
        // Arrange
        HttpClient client = _client;

        ImportOrcaBundleDto request = new ImportOrcaBundleDto
        {
            BundleJson = ""
        };

        StringContent content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Preview handles multiple presets per section")]
    public async Task Preview_MultiplePresetsPerSection_ParsesAll()
    {
        // Arrange
        HttpClient client = _client;

        string bundleJson = """
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

        ImportOrcaBundleDto request = new ImportOrcaBundleDto { BundleJson = bundleJson };
        StringContent content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        // Act
        HttpResponseMessage response = await client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string responseBody = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(responseBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Printers.Should().HaveCount(2);
        _ = preview.Filaments.Should().HaveCount(3);
        _ = preview.Processes.Should().HaveCount(3);
    }
}
