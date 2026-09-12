using System.Net;
using System.Net.Sockets;
using System.Text;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using FluentAssertions;

namespace Farm.Infrastructure.Tests.Discovery;

public class SocketFreeDiscoveryTests
{
    [Fact]
    public async Task DiscoverPrinterAsync_WithHttpPrinter_DiscoversWithoutContainerControl()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string?> request = RespondAsync(listener, timeout.Token);
        var service = new CoreNetworkDiscoveryService([new LoopbackProbe(port)]);

        var printer = await service.DiscoverPrinterAsync(
            IPAddress.Loopback.ToString(), 5000, cancellationToken: timeout.Token);

        printer.Should().NotBeNull();
        printer!.Backend.Should().Be(PrinterBackend.Moonraker);
        printer.BackendPort.Should().Be(port);
        printer.IpAddress.Should().Be(IPAddress.Loopback.ToString());
        (await request).Should().Be("GET /printer/info HTTP/1.1");
    }

    private static async Task<string?> RespondAsync(TcpListener listener, CancellationToken token)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(token);
        await using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        string? request = await reader.ReadLineAsync(token);
        while (await reader.ReadLineAsync(token) is { Length: > 0 })
        {
        }

        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        await stream.WriteAsync(response, token);
        return request;
    }

    private sealed class LoopbackProbe(int port) : BaseDiscoveryProbe
    {
        public override string DisplayName => "Loopback HTTP printer";
        protected override int[] Ports => [port];
        protected override string EndpointPath => "/printer/info";
        protected override PrinterBackend Backend => PrinterBackend.Moonraker;
        protected override string PrinterName => "Loopback printer";
    }
}
