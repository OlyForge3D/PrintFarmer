using System.Net;
using System.Text;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests.Services.SmartPlug;

/// <summary>
/// Unit tests for <see cref="HomeAssistantSmartPlugProvider"/> covering token validation,
/// entity state parsing, and connectivity checks.
/// </summary>
public class HomeAssistantSmartPlugProviderTests
{
    private const string ValidToken = "test-ha-token-abc123";

    private static (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) CreateProvider(
        string? token = ValidToken)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
#pragma warning disable CA2000
        HttpClient httpClient = new(handler.Object);
#pragma warning restore CA2000

        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("SmartPlug")).Returns(httpClient);

        Dictionary<string, string?> configData = new()
        {
            ["HomeAssistant:Token"] = token
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Scope factory is only used when IConfiguration token is absent.
        // Always wire it up to return empty settings so the fallback path returns null cleanly.
        HomeAssistantSettings emptySettings = new();
        Mock<ISettingsService> emptySettingsService = new();
        emptySettingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(emptySettings);

        Mock<IServiceProvider> emptyServiceProvider = new();
        emptyServiceProvider.Setup(sp => sp.GetService(typeof(ISettingsService))).Returns(emptySettingsService.Object);

        Mock<IServiceScope> emptyScope = new();
        emptyScope.Setup(s => s.ServiceProvider).Returns(emptyServiceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(emptyScope.Object);

        Mock<ISensitiveDataProtector> dataProtector = new();

        HomeAssistantSmartPlugProvider provider = new(
            factory.Object,
            config,
            scopeFactory.Object,
            dataProtector.Object,
            NullLogger<HomeAssistantSmartPlugProvider>.Instance);

        return (provider, handler);
    }

    /// <summary>
    /// Creates a provider that has no config token but has a persisted encrypted token via settings.
    /// </summary>
    private static (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) CreateProviderWithPersistedToken(
        string plainToken)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
#pragma warning disable CA2000
        HttpClient httpClient = new(handler.Object);
#pragma warning restore CA2000

        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("SmartPlug")).Returns(httpClient);

        IConfiguration config = new ConfigurationBuilder().Build(); // no token in config

        string fakeEncrypted = $"enc:{plainToken}";

        Mock<ISensitiveDataProtector> dataProtector = new();
        dataProtector.Setup(p => p.Unprotect(fakeEncrypted)).Returns(plainToken);

        HomeAssistantSettings settingsWithToken = new() { EncryptedToken = fakeEncrypted, BaseUrl = "http://ha.local:8123", Enabled = true };

        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(settingsWithToken);

        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider.GetService(typeof(ISettingsService))).Returns(settingsService.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        HomeAssistantSmartPlugProvider provider = new(
            factory.Object,
            config,
            scopeFactory.Object,
            dataProtector.Object,
            NullLogger<HomeAssistantSmartPlugProvider>.Instance);

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
    public async Task GetCurrentReadingAsync_WhenTokenMissing_ReturnsNull()
    {
        (HomeAssistantSmartPlugProvider provider, _) = CreateProvider(token: null);

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
    public async Task TestConnectionAsync_WhenTokenMissing_ReturnsFalse()
    {
        (HomeAssistantSmartPlugProvider provider, _) = CreateProvider(token: null);

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
    public async Task GetCurrentReadingAsync_WithLegacyAddressFormat_UsesDefaultBaseUrl()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

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

    [Fact]
    public async Task GetCurrentReadingAsync_WhenTokenFromPersistedSettings_ReturnsPowerReading()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProviderWithPersistedToken(ValidToken);

        string json = """{"entity_id":"sensor.plug_power","state":"55.0","attributes":{}}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://ha.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().NotBeNull();
        reading!.WattsNow.Should().BeApproximately(55.0, 0.001);
    }
}
