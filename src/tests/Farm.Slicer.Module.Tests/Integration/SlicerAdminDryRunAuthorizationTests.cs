using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Regression tests ensuring every action on <c>SlicerAdminController</c> — POST
/// /api/admin/slicer/dry-run and GET/PUT /api/admin/slicer/settings — requires
/// farm_admin authorization via both the class-level <c>[Authorize]</c> gate and the
/// <c>RequirePermission("slicer_engines:admin")</c> filter, matching the sibling
/// <c>SlicerManagementController</c> pattern.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Regression")]
public class SlicerAdminDryRunAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    private static HttpRequestMessage CreateDryRunRequest() =>
        new(HttpMethod.Post, "/api/admin/slicer/dry-run")
        {
            Content = JsonContent.Create(new { template = "{filename}{extension}" }),
        };

    private static HttpRequestMessage CreateGetSettingsRequest() =>
        new(HttpMethod.Get, "/api/admin/slicer/settings");

    private static HttpRequestMessage CreateUpdateSettingsRequest() =>
        new(HttpMethod.Put, "/api/admin/slicer/settings")
        {
            Content = JsonContent.Create(new { enabled = true, jitterPercent = 0 }),
        };

    [Fact]
    public async Task DryRun_WithoutAuthentication_Returns401()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = CreateDryRunRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DryRun_AuthenticatedNonAdmin_Returns403()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync();
        using HttpRequestMessage request = CreateDryRunRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DryRun_FarmAdmin_ReturnsOk()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        using HttpRequestMessage request = CreateDryRunRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSettings_WithoutAuthentication_Returns401()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = CreateGetSettingsRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSettings_AuthenticatedNonAdmin_Returns403()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync();
        using HttpRequestMessage request = CreateGetSettingsRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSettings_FarmAdmin_ReturnsOk()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        using HttpRequestMessage request = CreateGetSettingsRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSettings_WithoutAuthentication_Returns401()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = CreateUpdateSettingsRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateSettings_AuthenticatedNonAdmin_Returns403()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync();
        using HttpRequestMessage request = CreateUpdateSettingsRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateSettings_FarmAdmin_ReturnsOk()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        using HttpRequestMessage request = CreateUpdateSettingsRequest();

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
