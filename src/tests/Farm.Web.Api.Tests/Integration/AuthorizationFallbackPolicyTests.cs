using System.Net;
using System.Net.Http.Json;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Integration;

[Collection(IntegrationTestCollection.Name)]
public class AuthorizationFallbackPolicyTests : IAsyncLifetime
{
    private CustomWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task AttributeLessEndpoint_Unauthenticated_Returns401()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.GetAsync("/api/debug/db-info");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Healthz_Unauthenticated_Returns200()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_Unauthenticated_DoesNotReturn401()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsJsonAsync("/api/auth/login", new { usernameOrEmail = "", password = "" });
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
