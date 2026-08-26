using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Regression tests for issue #2049: <c>filament/for-machines</c> and
/// <c>process/for-machines</c> must be able to return a lightweight projection (no
/// <c>StartGcode</c>/<c>EndGcode</c>/<c>Settings</c>) for calibration/list clients via
/// <c>?view=summary</c>, while preserving the full profile by default for callers that still
/// need it (slice submission, export, clone).
/// </summary>
public class ProfilesControllerForMachinesSummaryTests
{
    [Fact]
    public async Task GetFilamentProfilesForMachinesAsync_DefaultView_ReturnsFullDtoWithSettingsAndGcode()
    {
        FilamentProfileDto fullProfile = CreateFilamentProfile();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        _ = profilesService
            .Setup(s => s.GetFilamentProfilesForMachinesAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync([fullProfile]);

        ProfilesController controller = CreateController(profilesService);
        using HttpClient httpClient = new();

        IActionResult result = await controller.GetFilamentProfilesForMachinesAsync(
            httpClient, new ForMachinesRequest { MachineNames = ["Qidi X-Plus 4 0.4 nozzle"] }, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IReadOnlyList<FilamentProfileDto> value = Assert.IsAssignableFrom<IReadOnlyList<FilamentProfileDto>>(ok.Value);
        FilamentProfileDto returned = Assert.Single(value);
        Assert.Equal("; START_GCODE_MARKER", returned.StartGcode);
        Assert.Equal("; END_GCODE_MARKER", returned.EndGcode);
        Assert.True(returned.Settings.ContainsKey("raw_setting_key"));
    }

    [Fact]
    public async Task GetFilamentProfilesForMachinesAsync_SummaryView_OmitsSettingsAndGcode()
    {
        FilamentProfileDto fullProfile = CreateFilamentProfile();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        _ = profilesService
            .Setup(s => s.GetFilamentProfilesForMachinesAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync([fullProfile]);

        ProfilesController controller = CreateController(profilesService);
        using HttpClient httpClient = new();

        IActionResult result = await controller.GetFilamentProfilesForMachinesAsync(
            httpClient, new ForMachinesRequest { MachineNames = ["Qidi X-Plus 4 0.4 nozzle"] }, CancellationToken.None, view: "summary");

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        List<FilamentProfileSummaryDto> value = Assert.IsType<List<FilamentProfileSummaryDto>>(ok.Value);
        FilamentProfileSummaryDto summary = Assert.Single(value);
        Assert.Equal("Generic PLA", summary.Name);
        Assert.Equal("PLA", summary.Material);
        Assert.Equal(["Qidi X-Plus 4 0.4 nozzle"], summary.CompatiblePrinters);

        // Serialize what actually crosses the wire and assert the opaque payload never appears.
        string json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("Settings", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartGcode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EndGcode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_setting_key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("START_GCODE_MARKER", json, StringComparison.Ordinal);
        Assert.DoesNotContain("END_GCODE_MARKER", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SUMMARY")]
    [InlineData("Summary")]
    public async Task GetFilamentProfilesForMachinesAsync_SummaryViewIsCaseInsensitive(string view)
    {
        FilamentProfileDto fullProfile = CreateFilamentProfile();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        _ = profilesService
            .Setup(s => s.GetFilamentProfilesForMachinesAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync([fullProfile]);

        ProfilesController controller = CreateController(profilesService);
        using HttpClient httpClient = new();

        IActionResult result = await controller.GetFilamentProfilesForMachinesAsync(
            httpClient, new ForMachinesRequest { MachineNames = ["Qidi X-Plus 4 0.4 nozzle"] }, CancellationToken.None, view: view);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        _ = Assert.IsType<List<FilamentProfileSummaryDto>>(ok.Value);
    }

    [Fact]
    public async Task GetProcessProfilesForMachinesAsync_DefaultView_ReturnsFullDtoWithSettings()
    {
        ProcessProfileDto fullProfile = CreateProcessProfile();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        _ = profilesService
            .Setup(s => s.GetProcessProfilesForMachinesAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync([fullProfile]);

        ProfilesController controller = CreateController(profilesService);
        using HttpClient httpClient = new();

        IActionResult result = await controller.GetProcessProfilesForMachinesAsync(
            httpClient, new ForMachinesRequest { MachineNames = ["Qidi X-Plus 4 0.4 nozzle"] }, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IReadOnlyList<ProcessProfileDto> value = Assert.IsAssignableFrom<IReadOnlyList<ProcessProfileDto>>(ok.Value);
        ProcessProfileDto returned = Assert.Single(value);
        Assert.True(returned.Settings.ContainsKey("raw_setting_key"));
    }

    [Fact]
    public async Task GetProcessProfilesForMachinesAsync_SummaryView_OmitsSettings()
    {
        ProcessProfileDto fullProfile = CreateProcessProfile();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        _ = profilesService
            .Setup(s => s.GetProcessProfilesForMachinesAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync([fullProfile]);

        ProfilesController controller = CreateController(profilesService);
        using HttpClient httpClient = new();

        IActionResult result = await controller.GetProcessProfilesForMachinesAsync(
            httpClient, new ForMachinesRequest { MachineNames = ["Qidi X-Plus 4 0.4 nozzle"] }, CancellationToken.None, view: "summary");

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        List<ProcessProfileSummaryDto> value = Assert.IsType<List<ProcessProfileSummaryDto>>(ok.Value);
        ProcessProfileSummaryDto summary = Assert.Single(value);
        Assert.Equal("0.20mm Standard", summary.Name);
        Assert.Equal("standard", summary.Quality);

        string json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("Settings", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_setting_key", json, StringComparison.Ordinal);
    }

    private static FilamentProfileDto CreateFilamentProfile() => new()
    {
        Name = "Generic PLA",
        Material = "PLA",
        Manufacturer = "Generic",
        CompatiblePrinters = ["Qidi X-Plus 4 0.4 nozzle"],
        NozzleTemperature = 210,
        BedTemperature = 60,
        PrintSpeed = 50,
        StartGcode = "; START_GCODE_MARKER",
        EndGcode = "; END_GCODE_MARKER",
        Settings = new Dictionary<string, object> { ["raw_setting_key"] = "raw_setting_value" },
    };

    private static ProcessProfileDto CreateProcessProfile() => new()
    {
        Name = "0.20mm Standard",
        Quality = "standard",
        CompatiblePrinters = ["Qidi X-Plus 4 0.4 nozzle"],
        LayerHeight = 0.2,
        InfillPercentage = 20,
        PrintSpeed = 50,
        Settings = new Dictionary<string, object> { ["raw_setting_key"] = "raw_setting_value" },
    };

    private static ProfilesController CreateController(Mock<IProfilesService> profilesService)
    {
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            profilesService.Object,
            catalogService.Object);
    }
}
