using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Ensures slicer-disabled tests run serially (env var side-effect).
/// </summary>
[CollectionDefinition("SlicerDisabled")]
public class SlicerDisabledCollection { }

/// <summary>
/// Integration tests that verify the API starts and behaves correctly when
/// the slicer module is not loaded (microservices deployment mode).
/// Uses <see cref="SlicerDisabledWebApplicationFactory"/> which sets
/// <c>DEPLOYMENT_MODE=microservices</c> before the host boots.
/// </summary>
[Collection("SlicerDisabled")]
public class SlicerDisabledIntegrationTests : IAsyncLifetime
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
    public async Task HealthCheck_WhenSlicerDisabled_ReturnsOk()
    {
        HttpResponseMessage response = await _client!.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get3DModels_WhenSlicerDisabled_ReturnsEmptyArray()
    {
        HttpClient authClient = await _factory!.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await authClient.GetAsync("/api/3d-models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        object[]? result = await response.Content.ReadFromJsonAsync<object[]>();
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Get3DModelsFolders_WhenSlicerDisabled_ReturnsEmptyArray()
    {
        HttpClient authClient = await _factory!.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await authClient.GetAsync("/api/3d-models/folders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        object[]? result = await response.Content.ReadFromJsonAsync<object[]>();
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task PostModelsQuery_WhenSlicerDisabled_ReturnsEmptyArray()
    {
        HttpClient authClient = await _factory!.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await authClient.PostAsync("/api/3d-models/query", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        object[]? result = await response.Content.ReadFromJsonAsync<object[]>();
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SlicerApiRoute_WhenSlicerDisabled_Returns404WithStructuredError()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/slicer/profiles/hierarchy");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SLICER_DISABLED");
    }

    [Fact]
    public async Task SlicersRoute_WhenSlicerDisabled_Returns404WithStructuredError()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/slicers");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SLICER_DISABLED");
    }

    [Fact]
    public async Task WorkersRoute_WhenSlicerDisabled_Returns404WithStructuredError()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/workers");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SLICER_DISABLED");
    }

    [Fact]
    public async Task SliceRoute_WhenSlicerDisabled_Returns404WithStructuredError()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/slice/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SLICER_DISABLED");
    }

    [Fact]
    public async Task AdminSlicerRoute_WhenSlicerDisabled_Returns404WithStructuredError()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/slicer/system/cleanup");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SLICER_DISABLED");
    }

    [Fact]
    public async Task ArtifactsRoute_WhenSlicerDisabled_Returns404WithStructuredError()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/artifacts");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SLICER_DISABLED");
    }

    [Fact]
    public async Task NonSlicerEndpoints_WhenSlicerDisabled_StillWork()
    {
        HttpClient authClient = await _factory!.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await authClient.GetAsync("/api/printers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrintersEndpoint_WhenPrinterHasObicoEnabled_ReturnsObicoEnabledFlag()
    {
        using AsyncServiceScope scope = _factory!.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer manufacturer = await db.Manufacturers.FirstOrDefaultAsync()
            ?? db.Manufacturers.Add(new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" }).Entity;
        await db.SaveChangesAsync();

        PrinterModel model = await db.PrinterModels.FirstOrDefaultAsync()
            ?? db.PrinterModels.Add(new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id }).Entity;
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "obico-printer",
            ServerUrl = "http://obico-printer.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            ObicoEnabled = true
        };

        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        HttpClient authClient = await _factory.CreateAuthenticatedClientAsync();
        HttpResponseMessage response = await authClient.GetAsync("/api/printers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement printerJson = document.RootElement.EnumerateArray()
            .First(element => element.GetProperty("id").GetGuid() == printer.Id);

        printerJson.TryGetProperty("obicoEnabled", out JsonElement obicoEnabled).Should().BeTrue();
        obicoEnabled.GetBoolean().Should().BeTrue();
    }
}
