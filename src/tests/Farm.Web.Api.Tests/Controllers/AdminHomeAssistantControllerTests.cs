using System.Net;
using System.Text;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="AdminHomeAssistantController"/> covering settings CRUD,
/// connection test, and entity discovery endpoints.
/// </summary>
public class AdminHomeAssistantControllerTests
{
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<ISensitiveDataProtector> _dataProtector = new();
    private readonly Mock<IHttpClientFactory> _httpClientFactory = new();
    private readonly Mock<HttpMessageHandler> _httpHandler = new();

    private AdminHomeAssistantController CreateController()
    {
#pragma warning disable CA2000
        HttpClient client = new(_httpHandler.Object);
#pragma warning restore CA2000
        _httpClientFactory.Setup(f => f.CreateClient("SmartPlug")).Returns(client);

        return new AdminHomeAssistantController(
            _settingsService.Object,
            _dataProtector.Object,
            _httpClientFactory.Object,
            NullLogger<AdminHomeAssistantController>.Instance);
    }

    // ─── GetSettings ──────────────────────────────────────────────────────────

    [Fact]
    public void GetSettings_WhenNoSettingsStored_ReturnsDefaultDto()
    {
        HomeAssistantSettings defaultSettings = new();
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(defaultSettings);
        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantSettingsDto> result = controller.GetSettings();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        HomeAssistantSettingsDto dto = Assert.IsType<HomeAssistantSettingsDto>(ok.Value);
        dto.Enabled.Should().BeFalse();
        dto.BaseUrl.Should().BeEmpty();
        dto.TokenMasked.Should().BeEmpty();
    }

    [Fact]
    public void GetSettings_WhenTokenStored_ReturnsMaskedToken()
    {
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>())
            .Returns(new HomeAssistantSettings
            {
                Enabled = true,
                BaseUrl = "http://ha.local:8123",
                EncryptedToken = "enc:abcdefghij"
            });
        _dataProtector.Setup(p => p.Unprotect("enc:abcdefghij")).Returns("abcdefghij");
        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantSettingsDto> result = controller.GetSettings();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        HomeAssistantSettingsDto dto = Assert.IsType<HomeAssistantSettingsDto>(ok.Value);
        dto.TokenMasked.Should().StartWith("***");
        dto.TokenMasked.Should().EndWith("ghij");
    }

    // ─── UpdateSettings ───────────────────────────────────────────────────────

    [Fact]
    public void UpdateSettings_WhenValidRequest_EncryptsAndPersistsToken()
    {
        HomeAssistantSettings stored = new();
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(stored);
        _dataProtector.Setup(p => p.Protect("my-plain-token")).Returns("enc:my-plain-token");
        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantSettingsDto> result = controller.UpdateSettings(new UpdateHomeAssistantSettingsRequest
        {
            Enabled = true,
            BaseUrl = "http://ha.local:8123",
            Token = "my-plain-token"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        _settingsService.Verify(s => s.Save(It.Is<HomeAssistantSettings>(
            x => x.EncryptedToken == "enc:my-plain-token" && x.Enabled && x.BaseUrl == "http://ha.local:8123")),
            Times.Once);
    }

    [Fact]
    public void UpdateSettings_WhenTokenIsMaskedPlaceholder_LeavesExistingTokenUnchanged()
    {
        HomeAssistantSettings stored = new() { EncryptedToken = "enc:existing" };
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(stored);
        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantSettingsDto> result = controller.UpdateSettings(new UpdateHomeAssistantSettingsRequest
        {
            Enabled = false,
            BaseUrl = "http://ha.local:8123",
            Token = "***abcd" // masked placeholder
        });

        Assert.IsType<OkObjectResult>(result.Result);
        // Protect must not be called; existing token is preserved.
        _dataProtector.Verify(p => p.Protect(It.IsAny<string>()), Times.Never);
        stored.EncryptedToken.Should().Be("enc:existing");
    }

    [Fact]
    public void UpdateSettings_WhenEnabledWithoutBaseUrl_ReturnsBadRequest()
    {
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(new HomeAssistantSettings());
        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantSettingsDto> result = controller.UpdateSettings(new UpdateHomeAssistantSettingsRequest
        {
            Enabled = true,
            BaseUrl = string.Empty,
            Token = "some-token"
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── TestConnectionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task TestConnectionAsync_WhenBaseUrlMissing_ReturnsFailure()
    {
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>())
            .Returns(new HomeAssistantSettings { EncryptedToken = "enc:tok" });
        _dataProtector.Setup(p => p.Unprotect("enc:tok")).Returns("tok");
        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantConnectionTestResult> result =
            await controller.TestConnectionAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        HomeAssistantConnectionTestResult dto = Assert.IsType<HomeAssistantConnectionTestResult>(ok.Value);
        dto.Success.Should().BeFalse();
        dto.Message.Should().Contain("base URL");
    }

    [Fact]
    public async Task TestConnectionAsync_WhenTokenMissing_ReturnsFailure()
    {
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>())
            .Returns(new HomeAssistantSettings { BaseUrl = "http://ha.local:8123" });
        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantConnectionTestResult> result =
            await controller.TestConnectionAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        HomeAssistantConnectionTestResult dto = Assert.IsType<HomeAssistantConnectionTestResult>(ok.Value);
        dto.Success.Should().BeFalse();
        dto.Message.Should().Contain("token");
    }

    [Fact]
    public async Task TestConnectionAsync_WhenHaResponds_ReturnsVersionAndEntityCount()
    {
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>())
            .Returns(new HomeAssistantSettings
            {
                BaseUrl = "http://ha.local:8123",
                EncryptedToken = "enc:tok"
            });
        _dataProtector.Setup(p => p.Unprotect("enc:tok")).Returns("mytoken");

        // First call: GET /api/ → version
        // Second call: GET /api/states → entity list
        string versionJson = """{"message":"API running.","version":"2024.1.0"}""";
        string statesJson = """
            [
                {"entity_id":"sensor.plug_power","state":"50.0","attributes":{"device_class":"power","unit_of_measurement":"W","friendly_name":"Plug Power"}},
                {"entity_id":"sensor.plug_energy","state":"1.5","attributes":{"device_class":"energy","unit_of_measurement":"kWh","friendly_name":"Plug Energy"}},
                {"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen Light"}}
            ]
            """;

        int callCount = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(callCount == 1 ? versionJson : statesJson, Encoding.UTF8, "application/json")
                };
            });

        AdminHomeAssistantController controller = CreateController();

        ActionResult<HomeAssistantConnectionTestResult> result =
            await controller.TestConnectionAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        HomeAssistantConnectionTestResult dto = Assert.IsType<HomeAssistantConnectionTestResult>(ok.Value);
        dto.Success.Should().BeTrue();
        dto.Version.Should().Be("2024.1.0");
        dto.PowerEntityCount.Should().Be(2); // sensor.plug_power and sensor.plug_energy only
    }

    // ─── DiscoverEntitiesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DiscoverEntitiesAsync_WhenBaseUrlMissing_ReturnsBadRequest()
    {
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>())
            .Returns(new HomeAssistantSettings { EncryptedToken = "enc:tok" });
        _dataProtector.Setup(p => p.Unprotect("enc:tok")).Returns("tok");
        AdminHomeAssistantController controller = CreateController();

        ActionResult<IEnumerable<HomeAssistantEntityDto>> result =
            await controller.DiscoverEntitiesAsync(CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task DiscoverEntitiesAsync_WhenHaResponds_ReturnsPowerEntitiesOnly()
    {
        _settingsService.Setup(s => s.Get<HomeAssistantSettings>())
            .Returns(new HomeAssistantSettings
            {
                BaseUrl = "http://ha.local:8123",
                EncryptedToken = "enc:tok"
            });
        _dataProtector.Setup(p => p.Unprotect("enc:tok")).Returns("mytoken");

        string statesJson = """
            [
                {"entity_id":"sensor.plug_power","state":"50.0","attributes":{"device_class":"power","unit_of_measurement":"W","friendly_name":"Plug Power"}},
                {"entity_id":"sensor.other_sensor","state":"22.1","attributes":{"device_class":"temperature","unit_of_measurement":"°C","friendly_name":"Temp"}},
                {"entity_id":"switch.printer_power","state":"on","attributes":{"unit_of_measurement":"W","friendly_name":"Printer Switch"}},
                {"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen"}}
            ]
            """;

        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(statesJson, Encoding.UTF8, "application/json")
            });

        AdminHomeAssistantController controller = CreateController();

        ActionResult<IEnumerable<HomeAssistantEntityDto>> result =
            await controller.DiscoverEntitiesAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        List<HomeAssistantEntityDto> entities = Assert.IsType<List<HomeAssistantEntityDto>>(ok.Value);
        entities.Should().HaveCount(2);
        entities.Should().Contain(e => e.EntityId == "sensor.plug_power");
        entities.Should().Contain(e => e.EntityId == "switch.printer_power");
        entities.Should().NotContain(e => e.EntityId == "light.kitchen");
        entities.Should().NotContain(e => e.EntityId == "sensor.other_sensor");
    }
}
