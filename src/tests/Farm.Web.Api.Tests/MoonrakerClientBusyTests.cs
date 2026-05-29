using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Tests that MoonrakerClient propagates firmware-level busy responses as
/// <see cref="PrinterBackendBusyException"/> (#317).
/// </summary>
public class MoonrakerClientBusyTests
{
    private static (MoonrakerClient client, Mock<HttpMessageHandler> handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => responder(req));

#pragma warning disable CA2000
        HttpClient http = new(handler.Object);
#pragma warning restore CA2000

        MoonrakerClient client = new(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        return (client, handler);
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns503_ThrowsPrinterBackendBusyException()
    {
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<PrinterBackendBusyException>(
            () => client.SendGcodeAsync("http://moonraker:7125", "M104 S200"));
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns409_ThrowsPrinterBackendBusyException()
    {
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        await Assert.ThrowsAsync<PrinterBackendBusyException>(
            () => client.SendGcodeAsync("http://moonraker:7125", "G28"));
    }

    [Fact]
    public async Task SetTemps_WhenFirmwareReturns503_ThrowsPrinterBackendBusyException()
    {
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<PrinterBackendBusyException>(
            () => client.SetTempsAsync("http://moonraker:7125", hotend: 200));
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns200_ReturnsTrueWithoutException()
    {
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        bool result = await client.SendGcodeAsync("http://moonraker:7125", "M84");
        result.Should().BeTrue();
    }
}
