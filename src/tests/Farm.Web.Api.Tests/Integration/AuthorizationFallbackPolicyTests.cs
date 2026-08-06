using System.Net;
using System.Net.Http.Json;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    public void FallbackPolicy_RequiresAuthenticatedUser()
    {
        AuthorizationOptions options = _factory.Services
            .GetRequiredService<IOptions<AuthorizationOptions>>()
            .Value;

        options.FallbackPolicy.Should().NotBeNull();
        options.FallbackPolicy!.Requirements.Should()
            .ContainSingle(requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Theory]
    [InlineData("/api/catalog/manufacturers")]
    [InlineData("/api/job-scheduling/timezones")]
    [InlineData("/api/gcode-files/file/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/3d-models/file/00000000-0000-0000-0000-000000000000")]
    public async Task ProtectedEndpoint_Unauthenticated_Returns401(string path)
    {
        using HttpClient anon = _factory.CreateClient();

        HttpResponseMessage response = await anon.GetAsync(path);

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
