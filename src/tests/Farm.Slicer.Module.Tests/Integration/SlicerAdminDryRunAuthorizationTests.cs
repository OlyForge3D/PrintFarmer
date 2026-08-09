using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Regression tests ensuring POST /api/admin/slicer/dry-run requires farm_admin
/// authorization, matching its sibling actions on <c>SlicerAdminController</c>
/// (GET/PUT /api/admin/slicer/settings).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Regression")]
[Collection(IntegrationTestCollection.Name)]
public class SlicerAdminDryRunAuthorizationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static HttpRequestMessage CreateDryRunRequest() =>
        new(HttpMethod.Post, "/api/admin/slicer/dry-run")
        {
            Content = JsonContent.Create(new { template = "{filename}{extension}" }),
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
}
