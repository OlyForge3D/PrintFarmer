using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests.Services.SmartPlug;

/// <summary>
/// Unit tests for <see cref="TasmotaSmartPlugProvider"/> using mocked HTTP handlers.
/// </summary>
public class TasmotaSmartPlugProviderTests
{
    private static (TasmotaSmartPlugProvider provider, Mock<HttpMessageHandler> handler) CreateProvider()
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
#pragma warning disable CA2000
        HttpClient httpClient = new(handler.Object);
#pragma warning restore CA2000

        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("SmartPlug")).Returns(httpClient);

        TasmotaSmartPlugProvider provider = new(factory.Object, NullLogger<TasmotaSmartPlugProvider>.Instance);
        return (provider, handler);
    }

    private static void SetupHandler(Mock<HttpMessageHandler> handler, HttpStatusCode status, string? json = null)
    {
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = json is not null
                    ? new StringContent(json, Encoding.UTF8, "application/json")
                    : new StringContent(string.Empty)
            });
    }

    [Fact]
    public void ProviderType_ShouldBeTasmota()
    {
        (TasmotaSmartPlugProvider provider, _) = CreateProvider();
        provider.ProviderType.Should().Be("Tasmota");
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenValidResponse_ReturnsPowerReading()
    {
        (TasmotaSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string json = """
            {
                "StatusSNS": {
                    "ENERGY": {
                        "Power": 45.2,
                        "Today": 0.123,
                        "Voltage": 230.1,
                        "Current": 0.196
                    }
                }
            }
            """;
        SetupHandler(handler, HttpStatusCode.OK, json);

        PowerReading? reading = await provider.GetCurrentReadingAsync("192.168.1.100", CancellationToken.None);

        reading.Should().NotBeNull();
        reading!.WattsNow.Should().BeApproximately(45.2, 0.001);
        reading.TotalKwh.Should().BeApproximately(0.123, 0.001);
        reading.Voltage.Should().BeApproximately(230.1, 0.001);
        reading.CurrentAmps.Should().BeApproximately(0.196, 0.001);
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenDeviceOffline_ReturnsNull()
    {
        (TasmotaSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        PowerReading? reading = await provider.GetCurrentReadingAsync("192.168.1.100", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenMissingEnergySection_ReturnsNull()
    {
        (TasmotaSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        SetupHandler(handler, HttpStatusCode.OK, """{"StatusSNS":{}}""");

        PowerReading? reading = await provider.GetCurrentReadingAsync("192.168.1.100", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenDeviceReachable_ReturnsTrue()
    {
        (TasmotaSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        SetupHandler(handler, HttpStatusCode.OK, """{"Status":{}}""");

        bool result = await provider.TestConnectionAsync("192.168.1.100", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenDeviceUnreachable_ReturnsFalse()
    {
        (TasmotaSmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        bool result = await provider.TestConnectionAsync("192.168.1.100", CancellationToken.None);

        result.Should().BeFalse();
    }
}
