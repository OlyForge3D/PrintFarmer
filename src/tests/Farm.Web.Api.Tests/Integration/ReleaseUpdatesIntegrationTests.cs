using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Services.ReleaseUpdates;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for <c>GET /api/admin/release-updates</c> (issue #3281).
/// </summary>
public class ReleaseUpdatesIntegrationTests : IClassFixture<ReleaseUpdatesIntegrationTests.Factory>, IAsyncLifetime
{
    private const string Endpoint = "/api/admin/release-updates";

    public class Factory : CustomWebApplicationFactory
    {
        public Factory()
            : base(new Dictionary<string, string?>
            {
                ["Security:DevModeBypassAuth"] = "false",
                [ApplicationReleaseUpdateState.InstalledVersionConfigurationKey] = "0.2.3-insider.5",
            })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Never reach GitHub from tests; the background check reads this fake instead.
                services.RemoveAll<IApplicationReleaseSource>();
                services.AddSingleton<IApplicationReleaseSource, FakeReleaseSource>();
            });
        }
    }

    private readonly Factory _factory;
    private HttpClient? _adminClient;
    private HttpClient? _nonAdminClient;

    public ReleaseUpdatesIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _adminClient = await _factory.CreateAdminClientAsync();
        _nonAdminClient = await _factory.CreateAuthenticatedClientAsync(
            username: "release-updates-user",
            email: "release-updates-user@example.com");
    }

    public Task DisposeAsync()
    {
        _adminClient?.Dispose();
        _nonAdminClient?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetStatus_Anonymous_Returns401()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetStatus_NonAdmin_Returns403()
    {
        using HttpResponseMessage response = await _nonAdminClient!.GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetStatus_Admin_ReturnsCachedCamelCaseStatusWithStringEnums()
    {
        ApplicationReleaseUpdateState state = _factory.Services.GetRequiredService<ApplicationReleaseUpdateState>();
        ApplicationReleaseVersion.TryParse("v0.2.3-insider.6", out ApplicationReleaseVersion? latest).Should().BeTrue();
        state.RecordSuccess(new ApplicationReleaseInfo(latest!, "Insider 6", null));

        using HttpResponseMessage response = await _adminClient!.GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("UpdateAvailable");
        root.GetProperty("updateAvailable").GetBoolean().Should().BeTrue();
        root.GetProperty("installedVersion").GetString().Should().Be("0.2.3-insider.5");
        root.GetProperty("channel").GetString().Should().Be("Insider");
        root.GetProperty("latestVersion").GetString().Should().Be("0.2.3-insider.6");
        root.GetProperty("latestTag").GetString().Should().Be("v0.2.3-insider.6");
        root.GetProperty("releaseUrl").GetString()
            .Should().Be("https://github.com/OlyForge3D/PrintFarmer/releases/tag/v0.2.3-insider.6");
        root.GetProperty("upgradeDocsUrl").GetString().Should().StartWith("https://github.com/OlyForge3D/PrintFarmer/blob/");
        root.GetProperty("isStale").GetBoolean().Should().BeFalse();
        root.TryGetProperty("lastSuccessfulCheckAt", out _).Should().BeTrue();
        root.TryGetProperty("checkIntervalSeconds", out _).Should().BeTrue();
    }

    private sealed class FakeReleaseSource : IApplicationReleaseSource
    {
        public Task<ApplicationReleaseInfo?> GetLatestReleaseAsync(
            ApplicationReleaseChannel channel,
            CancellationToken cancellationToken)
        {
            ApplicationReleaseVersion.TryParse("v0.2.3-insider.6", out ApplicationReleaseVersion? version);
            return Task.FromResult<ApplicationReleaseInfo?>(new ApplicationReleaseInfo(version!, "Insider 6", null));
        }
    }
}
