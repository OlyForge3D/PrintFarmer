using System.Net;
using System.Text;
using Farm.Web.Api.Services.HomeAssistant;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests.Services.SmartPlug;

/// <summary>
/// Unit tests for <see cref="HomeAssistantSmartPlugProvider"/> covering settings resolution,
/// entity state parsing, and connectivity checks.
/// </summary>
public class HomeAssistantSmartPlugProviderTests
{
    private const string DefaultBaseUrl = "http://homeassistant.local:8123";
    private const string ValidToken = "test-ha-token-abc123";

    private static (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) CreateProvider(
        bool hasConfig = true, string baseUrl = DefaultBaseUrl)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
#pragma warning disable CA2000
        HttpClient httpClient = new(handler.Object);
#pragma warning restore CA2000

        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("SmartPlug")).Returns(httpClient);

        Mock<IHomeAssistantSettingsProvider> settingsProvider = new();
        settingsProvider
            .Setup(s => s.GetEnabledConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasConfig
                ? new HomeAssistantConnectionConfig(baseUrl, ValidToken)
                : null);

        HomeAssistantSmartPlugProvider provider = new(
            factory.Object, settingsProvider.Object, NullLogger<HomeAssistantSmartPlugProvider>.Instance);

        return (provider, handler);
    }

    [Fact]
    public void ProviderType_ShouldBeHomeAssistant()
    {
        (HomeAssistantSmartPlugProvider provider, _) = CreateProvider();
        provider.ProviderType.Should().Be("HomeAssistant");
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenValidState_ReturnsPowerReading()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string stateJson = """
            {
                "entity_id": "sensor.plug_power",
                "state": "72.4",
                "attributes": {
                    "voltage": 230.5,
                    "current": 0.314,
                    "energy": 1.05
                }
            }
            """;

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stateJson, Encoding.UTF8, "application/json")
            });

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().NotBeNull();
        reading!.WattsNow.Should().BeApproximately(72.4, 0.001);
        reading.Voltage.Should().BeApproximately(230.5, 0.001);
        reading.CurrentAmps.Should().BeApproximately(0.314, 0.001);
        reading.TotalKwh.Should().BeApproximately(1.05, 0.001);
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenSettingsNotConfigured_ReturnsNull()
    {
        (HomeAssistantSmartPlugProvider provider, _) = CreateProvider(hasConfig: false);

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenStateIsUnavailable_ReturnsNull()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string json = """{"entity_id":"sensor.plug_power","state":"unavailable","attributes":{}}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenDeviceOffline_ReturnsNull()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenSettingsNotConfigured_ReturnsFalse()
    {
        (HomeAssistantSmartPlugProvider provider, _) = CreateProvider(hasConfig: false);

        bool result = await provider.TestConnectionAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenApiResponds_ReturnsTrue()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":"API running."}""", Encoding.UTF8, "application/json")
            });

        bool result = await provider.TestConnectionAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WithLegacyAddressFormat_UsesBaseUrlFromSettings()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider(
            baseUrl: DefaultBaseUrl);

        string json = """{"entity_id":"sensor.plug_power","state":"10.0","attributes":{}}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == "homeassistant.local"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        PowerReading? reading = await provider.GetCurrentReadingAsync("sensor.plug_power", CancellationToken.None);

        reading.Should().NotBeNull();
        reading!.WattsNow.Should().BeApproximately(10.0, 0.001);
    }
}
