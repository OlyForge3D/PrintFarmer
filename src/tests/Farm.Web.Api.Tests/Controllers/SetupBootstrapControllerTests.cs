using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Setup;
using Farm.Infrastructure.Services.Setup;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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
            ["Spoolman:BaseUrl"] = "http://setup-user:setup-password@spoolman.local:7912?token=setup-token",
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
