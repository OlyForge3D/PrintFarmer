using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Dtos;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for the <c>/api/system/capabilities</c> endpoint.
/// This endpoint controls frontend feature gating — if it reports
/// <c>slicingEnabled = false</c>, the entire slicer UI is hidden.
/// </summary>
public class SystemCapabilitiesIntegrationTests : IAsyncLifetime
{
    private CustomWebApplicationFactory? _factory;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Capabilities_ReturnsOk()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Capabilities_IsUnauthenticated_DoesNotRequireLogin()
    {
        // Capabilities must be accessible without auth — frontend reads it before login
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PlatformCapabilitiesDto? dto = await response.Content.ReadFromJsonAsync<PlatformCapabilitiesDto>();
        dto.Should().NotBeNull();
        dto!.Architecture.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Capabilities_InStandardMode_ReportsSlicingEnabled()
    {
        // Standard (non-microservices) mode should have slicing enabled
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PlatformCapabilitiesDto? dto = await response.Content.ReadFromJsonAsync<PlatformCapabilitiesDto>();
        dto.Should().NotBeNull();
        dto!.SlicingEnabled.Should().BeTrue(
            "standard deployment mode loads the slicer module inline");
    }

    [Fact]
    public async Task Capabilities_ReportsArchitecture()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        PlatformCapabilitiesDto? dto = await response.Content.ReadFromJsonAsync<PlatformCapabilitiesDto>();
        dto.Should().NotBeNull();
        dto!.Architecture.Should().BeOneOf("X64", "X86", "Arm64", "Arm");
    }

    [Fact]
    public async Task Capabilities_ReportsGcodeUploadAlwaysEnabled()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        PlatformCapabilitiesDto? dto = await response.Content.ReadFromJsonAsync<PlatformCapabilitiesDto>();
        dto.Should().NotBeNull();
        dto!.GcodeUploadEnabled.Should().BeTrue("gcode upload has no native dependencies");
    }

    [Fact]
    public async Task Capabilities_InStandardMode_ReportsModelFilesEnabled()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        PlatformCapabilitiesDto? dto = await response.Content.ReadFromJsonAsync<PlatformCapabilitiesDto>();
        dto.Should().NotBeNull();
        dto!.ModelFilesEnabled.Should().BeTrue(
            "standard x64 mode should have model files enabled");
    }
}

/// <summary>
/// Capabilities endpoint tests when DEPLOYMENT_MODE=microservices.
/// Documents the current behavior: slicing is reported as disabled even when
/// a slicer-host worker is available. This is a known issue — the capabilities
/// endpoint reads from IConfiguration which is set at startup before any worker
/// registers, while the settings endpoint reads from ISettingsService which gets
/// updated dynamically by worker registration.
/// </summary>
[Collection("SlicerDisabled")]
public class SystemCapabilitiesMicroservicesTests : IAsyncLifetime
{
    private SlicerDisabledWebApplicationFactory? _factory;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        _factory = new SlicerDisabledWebApplicationFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Capabilities_InMicroservicesMode_SlicingCapabilityShouldNotBeForcedOff()
    {
        // REGRESSION GUARD: In microservices mode, the slicer module runs in a
        // separate slicer-host process. The capabilities endpoint must NOT report
        // slicingEnabled=false just because DEPLOYMENT_MODE=microservices.
        //
        // KNOWN PRODUCTION BUG: Program.cs line 101 sets slicerEnabled=false when
        // DEPLOYMENT_MODE=microservices, then line 141 writes Slicer:Enabled="False"
        // to IConfiguration. In production, the SystemCapabilitiesController reads
        // this "False" value and tells the frontend to hide all slicer UI — even when
        // a healthy slicer-host is running (as confirmed by /api/settings showing
        // Slicer.enabled=true from the modular settings service).
        //
        // This test verifies the CORRECT behavior: capabilities should report
        // slicingEnabled=true regardless of DEPLOYMENT_MODE, because the slicer
        // capability depends on worker availability, not the module loading mode.
        // The production bug is that builder.Configuration["Slicer:Enabled"] = "False"
        // at line 141 bleeds into the capabilities response.
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PlatformCapabilitiesDto? dto = await response.Content.ReadFromJsonAsync<PlatformCapabilitiesDto>();
        dto.Should().NotBeNull();

        // The capabilities flag must NOT be forced off by DEPLOYMENT_MODE alone.
        // If this test starts failing, it means the production bug has been
        // introduced into the test environment too.
        dto!.SlicingEnabled.Should().BeTrue(
            "microservices mode should not force slicingEnabled=false — the slicer "
            + "runs in a separate host and its availability should be independent of "
            + "the local module loading decision");
    }

    [Fact]
    public async Task Capabilities_InMicroservicesMode_StillAccessibleWithoutAuth()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Capabilities_InMicroservicesMode_OtherFeaturesUnaffected()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/system/capabilities");

        PlatformCapabilitiesDto? dto = await response.Content.ReadFromJsonAsync<PlatformCapabilitiesDto>();
        dto.Should().NotBeNull();

        // Model files and gcode upload should be unaffected by microservices mode
        dto!.ModelFilesEnabled.Should().BeTrue();
        dto!.GcodeUploadEnabled.Should().BeTrue();
    }
}
