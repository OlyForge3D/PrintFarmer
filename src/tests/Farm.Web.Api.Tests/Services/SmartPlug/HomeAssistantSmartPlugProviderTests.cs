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
        string? token = ValidToken, string? settingsBaseUrl = null)
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

        // Scope factory is always wired; token=null tests verify that null token → null reading.
        HomeAssistantSettings settings = new()
        {
            BaseUrl = settingsBaseUrl ?? string.Empty,
            Enabled = true
        };
        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(settings);

        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(sp => sp.GetService(typeof(ISettingsService))).Returns(settingsService.Object);

        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

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

    // Blocker 5: legacy address format (entity-only, no pipe) must use the configured
    // base URL from HomeAssistantSettings — never a hardcoded fallback host.
    [Fact]
    public async Task GetCurrentReadingAsync_WithLegacyAddressFormat_UsesConfiguredBaseUrl()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) =
            CreateProvider(settingsBaseUrl: "http://ha.custom.local:8123");

        string json = """{"entity_id":"sensor.plug_power","state":"10.0","attributes":{}}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == "ha.custom.local"),
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
    public async Task GetCurrentReadingAsync_WithLegacyAddressFormat_WhenBaseUrlNotConfigured_ReturnsNull()
    {
        // No settingsBaseUrl → configured base URL is empty → provider cannot resolve host.
        (HomeAssistantSmartPlugProvider provider, _) = CreateProvider(settingsBaseUrl: null);

        PowerReading? reading = await provider.GetCurrentReadingAsync("sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
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

    // ─── Blocker 2: Enabled toggle ────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentReadingAsync_WhenIntegrationDisabled_ReturnsNull()
    {
        // No config token override; settings.Enabled=false → provider must not poll.
        IConfiguration config = new ConfigurationBuilder().Build();

        HomeAssistantSettings disabledSettings = new()
        {
            Enabled = false,
            BaseUrl = "http://ha.local:8123",
            EncryptedToken = "enc:some-token"
        };

        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(disabledSettings);

        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(sp => sp.GetService(typeof(ISettingsService))).Returns(settingsService.Object);

        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

#pragma warning disable CA2000
        HttpClient httpClient = new(new Mock<HttpMessageHandler>(MockBehavior.Strict).Object);
#pragma warning restore CA2000
        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("SmartPlug")).Returns(httpClient);

        HomeAssistantSmartPlugProvider provider = new(
            factory.Object,
            config,
            scopeFactory.Object,
            new Mock<ISensitiveDataProtector>().Object,
            NullLogger<HomeAssistantSmartPlugProvider>.Instance);

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://ha.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }

    // ─── Blocker 6: error path coverage ──────────────────────────────────────

    [Fact]
    public async Task GetCurrentReadingAsync_WhenHaReturns401_ReturnsNull()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenHaReturns404_ReturnsNull()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenHaTimesOut_ReturnsNull()
    {
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Request timeout"));

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://homeassistant.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }

    // ─── Blocker 1 (round-3): kW → watts conversion ───────────────────────────

    [Fact]
    public async Task GetCurrentReadingAsync_WhenStateInKilowatts_ConvertsToWatts()
    {
        // HA entity reporting unit_of_measurement="kW" with state 1.5 must be stored as 1500 W.
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string stateJson = """
            {
                "entity_id": "sensor.plug_power",
                "state": "1.5",
                "attributes": {
                    "unit_of_measurement": "kW"
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
        reading!.WattsNow.Should().BeApproximately(1500.0, 0.001);
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenStateInWatts_DoesNotConvert()
    {
        // HA entity reporting unit_of_measurement="W" with state 250 must be stored as 250 W (no conversion).
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string stateJson = """
            {
                "entity_id": "sensor.plug_power",
                "state": "250.0",
                "attributes": {
                    "unit_of_measurement": "W"
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
        reading!.WattsNow.Should().BeApproximately(250.0, 0.001);
    }

    [Theory]
    [InlineData("kw")]
    [InlineData("KW")]
    public async Task GetCurrentReadingAsync_WhenStateInKilowattsCaseVariant_ConvertsToWatts(string unit)
    {
        // HA returns user-configured unit strings; "kw" and "KW" must be treated the same as "kW".
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string stateJson = $$"""
            {
                "entity_id": "sensor.plug_power",
                "state": "2.0",
                "attributes": {
                    "unit_of_measurement": "{{unit}}"
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
        reading!.WattsNow.Should().BeApproximately(2000.0, 0.001);
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenStateInMilliwatts_ConvertsToWatts()
    {
        // HA device_class=power supports mW; 500 mW must be stored as 0.5 W.
        (HomeAssistantSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string stateJson = """
            {
                "entity_id": "sensor.plug_power",
                "state": "500.0",
                "attributes": {
                    "unit_of_measurement": "mW"
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
        reading!.WattsNow.Should().BeApproximately(0.5, 0.001);
    }

    // ─── Blocker 2 (round-3): Enabled=false + env var set → provider is inert ─

    [Fact]
    public async Task GetCurrentReadingAsync_WhenIntegrationDisabledAndEnvVarSet_ReturnsNullWithoutHttpCall()
    {
        // Enabled=false must take priority over PFARM__HomeAssistant__Token.
        // The strict HttpMessageHandler proves that no outbound HTTP call is attempted.
        Dictionary<string, string?> configData = new()
        {
            ["HomeAssistant:Token"] = "env-override-token"
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        HomeAssistantSettings disabledSettings = new()
        {
            Enabled = false,
            BaseUrl = "http://ha.local:8123"
        };

        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<HomeAssistantSettings>()).Returns(disabledSettings);

        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(sp => sp.GetService(typeof(ISettingsService))).Returns(settingsService.Object);

        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        // Strict mock: any HTTP call throws, proving the provider is completely inert when disabled.
        Mock<HttpMessageHandler> strictHandler = new(MockBehavior.Strict);
#pragma warning disable CA2000
        HttpClient httpClient = new(strictHandler.Object);
#pragma warning restore CA2000
        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("SmartPlug")).Returns(httpClient);

        HomeAssistantSmartPlugProvider provider = new(
            factory.Object,
            config,
            scopeFactory.Object,
            new Mock<ISensitiveDataProtector>().Object,
            NullLogger<HomeAssistantSmartPlugProvider>.Instance);

        PowerReading? reading = await provider.GetCurrentReadingAsync(
            "http://ha.local:8123|sensor.plug_power", CancellationToken.None);

        reading.Should().BeNull();
    }
}
