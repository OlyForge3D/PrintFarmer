using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// HTTP-level authorization and response-shape coverage for the first-run bootstrap contract.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SetupBootstrapControllerTests : IAsyncLifetime
{
    private const string DeploymentBaseUrl = "http://spoolman.deployment.local:7912";
    private CustomWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            ["Spoolman:BaseUrl"] = DeploymentBaseUrl,
        });
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task GetBootstrapAsync_SetupRequired_ReturnsOnlyConfiguredBaseUrl()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/setup/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("baseUrl");
        body.RootElement.GetProperty("baseUrl").GetString().Should().Be(DeploymentBaseUrl);
        body.RootElement.TryGetProperty("barcodeScanDebugLoggingEnabled", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetBootstrapAsync_SetupComplete_Returns404AndNoConfiguration()
    {
        using HttpClient adminClient = await _factory.CreateAdminClientAsync();
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/setup/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("baseUrl");
        body.Contains("spoolman", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task GetSpoolmanSettingsAsync_SetupRequiredAndUnauthenticated_RemainsProtected()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/settings/Spoolman");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
