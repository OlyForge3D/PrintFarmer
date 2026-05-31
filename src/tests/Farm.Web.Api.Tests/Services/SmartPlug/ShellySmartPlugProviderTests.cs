using System.Net;
using System.Text;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests.Services.SmartPlug;

/// <summary>
/// Unit tests for <see cref="ShellySmartPlugProvider"/> covering Gen 1 and Gen 2 device paths.
/// </summary>
public class ShellySmartPlugProviderTests
{
    private static (ShellySmartPlugProvider provider, Mock<HttpMessageHandler> handler) CreateProvider()
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
#pragma warning disable CA2000
        HttpClient httpClient = new(handler.Object);
#pragma warning restore CA2000

        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("SmartPlug")).Returns(httpClient);

        ShellySmartPlugProvider provider = new(factory.Object, NullLogger<ShellySmartPlugProvider>.Instance);
        return (provider, handler);
    }

    [Fact]
    public void ProviderType_ShouldBeShelly()
    {
        (ShellySmartPlugProvider provider, _) = CreateProvider();
        provider.ProviderType.Should().Be("Shelly");
    }

    [Fact]
    public async Task GetCurrentReadingAsync_Gen2Device_ReturnsPowerReading()
    {
        (ShellySmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string gen2Json = """
            {
                "apower": 55.3,
                "voltage": 229.8,
                "current": 0.240,
                "aenergy": { "total": 1234 }
            }
            """;

        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(gen2Json, Encoding.UTF8, "application/json")
            });

        PowerReading? reading = await provider.GetCurrentReadingAsync("192.168.1.50", CancellationToken.None);

        reading.Should().NotBeNull();
        reading!.WattsNow.Should().BeApproximately(55.3, 0.001);
        reading.Voltage.Should().BeApproximately(229.8, 0.001);
        reading.CurrentAmps.Should().BeApproximately(0.240, 0.001);
        reading.TotalKwh.Should().BeApproximately(1.234, 0.001);
    }

    [Fact]
    public async Task GetCurrentReadingAsync_Gen1Device_WhenGen2Fails_ReturnsPowerReading()
    {
        (ShellySmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        string gen1Json = """{"power":30.0,"is_valid":true,"total":5678}""";

        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            // Gen 2 endpoint returns 404
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound))
            // Gen 1 endpoint returns data
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(gen1Json, Encoding.UTF8, "application/json")
            });

        PowerReading? reading = await provider.GetCurrentReadingAsync("192.168.1.50", CancellationToken.None);

        reading.Should().NotBeNull();
        reading!.WattsNow.Should().BeApproximately(30.0, 0.001);
        reading.TotalKwh.Should().BeApproximately(5.678, 0.001);
        reading.Voltage.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentReadingAsync_WhenBothEndpointsFail_ReturnsNull()
    {
        (ShellySmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Offline"))
            .ThrowsAsync(new HttpRequestException("Offline"));

        PowerReading? reading = await provider.GetCurrentReadingAsync("192.168.1.50", CancellationToken.None);

        reading.Should().BeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenShellyEndpointResponds_ReturnsTrue()
    {
        (ShellySmartPlugProvider provider, Mock<HttpMessageHandler> handler) = CreateProvider();

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"type":"SHPLG-S"}""", Encoding.UTF8, "application/json")
            });

        bool result = await provider.TestConnectionAsync("192.168.1.50", CancellationToken.None);

        result.Should().BeTrue();
    }
}
