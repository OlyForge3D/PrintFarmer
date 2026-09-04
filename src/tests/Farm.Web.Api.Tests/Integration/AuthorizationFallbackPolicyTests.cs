using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Tests.Integration;

public class AuthorizationFallbackPolicyTests : IClassFixture<AuthorizationFallbackPolicyTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory()
            : base(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" })
        {
        }
    }

    private readonly Factory _factory;

    public AuthorizationFallbackPolicyTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
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
    [InlineData("/api/3d-models/download-for-viewer?path=missing.stl")]
    [InlineData("/api/system/farm-shape")]
    public async Task ProtectedEndpoint_Unauthenticated_Returns401(string path)
    {
        using HttpClient anon = _factory.CreateClient();

        HttpResponseMessage response = await anon.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void ArtifactStaticServing_DefaultConfiguration_IsDisabled()
    {
        ArtifactStorageSettings settings = _factory.Services
            .GetRequiredService<IOptions<ArtifactStorageSettings>>()
            .Value;

        settings.EnableStaticServing.Should().BeFalse();
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
