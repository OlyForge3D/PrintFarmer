using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

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
}
