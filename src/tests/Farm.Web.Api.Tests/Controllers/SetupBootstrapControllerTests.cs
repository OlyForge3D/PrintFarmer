using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Setup;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Setup;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// HTTP-level authorization and response-shape coverage for the first-run bootstrap contract.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SetupBootstrapControllerTests : IClassFixture<SetupBootstrapControllerTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory() : base(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            ["Spoolman:BaseUrl"] = DeploymentBaseUrl,
            ["Setup:BootstrapSecret"] = new string('x', 32),
        })
        {
        }
    }

    private const string DeploymentBaseUrl = "http://spoolman.deployment.local:7912";
    private readonly Factory _factory;

    public SetupBootstrapControllerTests(Factory factory)
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("incorrect-proof")]
    public async Task CreateInitialAdminAsync_WithoutValidProof_DoesNotCreateUser(string? proof)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/setup/initial-admin", new CreateInitialAdminRequest
        {
            Username = "untrusted",
            Email = "untrusted@example.test",
            Password = "ValidPassword123!",
            BootstrapSecret = proof
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Users.AnyAsync(u => u.Username == "untrusted")).Should().BeFalse();
        (await db.UserRoles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task CreateInitialAdminAsync_ValidProofThenReplay_CreatesOnlyOneAdmin()
    {
        using HttpClient client = _factory.CreateClient();
        var request = new CreateInitialAdminRequest
        {
            Username = "installer",
            Email = "installer@example.test",
            Password = "ValidPassword123!",
            BootstrapSecret = new string('x', 32)
        };
        using HttpResponseMessage first = await client.PostAsJsonAsync("/api/setup/initial-admin", request);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        request.Username = "replay";
        request.Email = "replay@example.test";
        using HttpResponseMessage replay = await client.PostAsJsonAsync("/api/setup/initial-admin", request);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.UserRoles.CountAsync(r => r.Role.Name == "farm_admin")).Should().Be(1);
        (await db.Users.AnyAsync(u => u.Username == "replay")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateInitialAdminAsync_ConcurrentAnonymousClaims_CreatesOnlyOneAdmin()
    {
        using HttpClient client = _factory.CreateClient();
        async Task<HttpResponseMessage> ClaimAsync(string username) =>
            await client.PostAsJsonAsync("/api/setup/initial-admin", new CreateInitialAdminRequest
            {
                Username = username,
                Email = $"{username}@example.test",
                Password = "ValidPassword123!",
                BootstrapSecret = new string('x', 32)
            });

        HttpResponseMessage[] responses = await Task.WhenAll(ClaimAsync("first"), ClaimAsync("second"));
        try
        {
            responses.Select(r => r.StatusCode).Should().BeEquivalentTo(
                new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.UserRoles.CountAsync(r => r.Role.Name == "farm_admin")).Should().Be(1);
            (await db.Users.CountAsync(u => u.Username == "first" || u.Username == "second")).Should().Be(1);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
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
    public async Task GetBootstrapAsync_CredentialBearingUrl_DoesNotExposeConfiguration()
    {
        await using var factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            ["Spoolman:BaseUrl"] = new UriBuilder("http", "spoolman.local", 7912)
            {
                UserName = "setup-user",
                Password = "setup-password",
                Query = "token=setup-token",
            }.Uri.ToString(),
        });
        await factory.ResetDataAsync();
        using HttpClient anonymousClient = factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/setup/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument body = JsonDocument.Parse(content);
        body.RootElement.GetProperty("baseUrl").GetString().Should().BeEmpty();
        content.Should().NotContain("setup-user");
        content.Should().NotContain("setup-password");
        content.Should().NotContain("setup-token");
    }

    [Fact]
    public async Task GetSpoolmanSettingsAsync_SetupRequiredAndUnauthenticated_RemainsProtected()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/settings/Spoolman");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSetupStatusAsync_ControllerActivation_DoesNotRequireSettingsService()
    {
        var setupService = new Mock<ISetupService>();
        setupService.Setup(service => service.NeedsSetupAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = new SetupController(setupService.Object);

        ActionResult<object> result = await controller.GetSetupStatusAsync(CancellationToken.None);

        OkObjectResult response = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        response.Value.Should().NotBeNull();
        JsonElement body = JsonSerializer.SerializeToElement(response.Value);
        body.GetProperty("needsSetup").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetBootstrapAsync_StoredBaseUrlIsNull_ReturnsRequiredEmptyStringProperty()
    {
        var setupService = new Mock<ISetupService>();
        setupService.Setup(service => service.NeedsSetupAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var settingsService = new Mock<ISettingsService>();
        settingsService.Setup(service => service.Get<SpoolmanSettings>())
            .Returns(new SpoolmanSettings { BaseUrl = null! });
        var controller = new SetupController(setupService.Object);

        ActionResult<SetupBootstrapResponse> result =
            await controller.GetBootstrapAsync(settingsService.Object, CancellationToken.None);

        OkObjectResult response = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        SetupBootstrapResponse body = response.Value.Should().BeOfType<SetupBootstrapResponse>().Subject;
        body.BaseUrl.Should().BeEmpty();
    }
}
