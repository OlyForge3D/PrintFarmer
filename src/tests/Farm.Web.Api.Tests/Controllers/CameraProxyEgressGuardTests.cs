using System.Net;
using System.Net.Sockets;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Regression coverage for issue #1964: the camera-proxy endpoints on both
/// <see cref="Farm.Web.Api.Controllers.CamerasController"/> and
/// <see cref="Farm.Web.Api.Controllers.PrintersController"/> must run the caller-supplied
/// camera target through <c>IEgressGuard</c> before fetching it, exactly like the
/// <c>PrintersController.TestConnectionAsync</c> path already covered by
/// <see cref="PrintersControllerTestConnectionEgressTests"/>. These are HTTP-level tests (not
/// unit tests against a mocked guard) so that the real <c>EgressGuard</c> DNS/IP classification
/// is exercised end-to-end and a loopback listener can prove the server-side socket was never
/// opened, not merely that the response code looks right.
/// </summary>
public sealed class CameraProxyEgressGuardTests
{
    // --- CamerasController: standalone camera (no PrinterGroup scoping) ----------------------

    [Fact]
    public async Task CamerasController_LoopbackTarget_ReturnsBadGateway_AndNeverConnects()
    {
        using var listener = new LoopbackHttpListener(respondSuccessfully: true);
        Guid cameraId = Guid.NewGuid();
        string target = $"http://127.0.0.1:{listener.Port}/";

        var cameras = new Mock<ICameraService>();
        cameras
            .Setup(s => s.FindByIdAsync(cameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Camera
            {
                Id = cameraId,
                Name = "Loopback camera",
                PrinterId = null,
                IsEnabled = true,
                StreamUrl = target,
                SnapshotUrl = target,
            });

        using var factory = new CameraProxyFactory(cameras, allowedNetworkRanges: null);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/{cameraId}/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("camera_target_invalid");
        listener.ConnectionCount.Should().Be(0, "the egress guard must deny the loopback target before any socket is opened");
    }

    [Fact]
    public async Task CamerasController_LinkLocalTarget_ReturnsBadGateway_AndNeverConnects()
    {
        // 169.254.169.254 is the well-known cloud instance-metadata address. No listener is
        // started here at all: a real connection attempt to this address in a CI sandbox could
        // hang or behave unpredictably, so the assertion relies solely on the egress guard
        // denying the target before any connection is attempted.
        Guid cameraId = Guid.NewGuid();
        const string target = "http://169.254.169.254/latest/meta-data/";

        var cameras = new Mock<ICameraService>();
        cameras
            .Setup(s => s.FindByIdAsync(cameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Camera
            {
                Id = cameraId,
                Name = "Link-local camera",
                PrinterId = null,
                IsEnabled = true,
                StreamUrl = target,
                SnapshotUrl = target,
            });

        using var factory = new CameraProxyFactory(cameras, allowedNetworkRanges: null);
        using HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/{cameraId}/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("camera_target_invalid");
    }

    [Fact]
    public async Task CamerasController_AllowlistedLoopbackTarget_ProxiesSuccessfully()
    {
        // Stands in for "legitimate public/LAN target still proxies successfully": the loopback
        // listener is explicitly allowlisted via ALLOWED_NETWORK_RANGES, mirroring how an
        // operator would allowlist a legitimate destination that would otherwise be classified
        // as loopback/link-local/multicast. This proves the guard is not over-blocking targets
        // it has been told are safe, and that the pinned-address request still round-trips the
        // upstream response correctly.
        using var listener = new LoopbackHttpListener(respondSuccessfully: true);
        Guid cameraId = Guid.NewGuid();
        string target = $"http://127.0.0.1:{listener.Port}/";

        var cameras = new Mock<ICameraService>();
        cameras
            .Setup(s => s.FindByIdAsync(cameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Camera
            {
                Id = cameraId,
                Name = "Allowlisted loopback camera",
                PrinterId = null,
                IsEnabled = true,
                StreamUrl = target,
                SnapshotUrl = target,
            });

        using var factory = new CameraProxyFactory(cameras, allowedNetworkRanges: "127.0.0.1");
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/{cameraId}/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("snapshot-bytes");
        listener.ConnectionCount.Should().Be(1);
    }

    // --- PrintersController: printer-attached camera proxy ------------------------------------

    [Fact]
    public async Task PrintersController_LoopbackTarget_ReturnsBadGateway_AndNeverConnects()
    {
        using var listener = new LoopbackHttpListener(respondSuccessfully: true);
        Guid printerId = Guid.NewGuid();
        string target = $"http://127.0.0.1:{listener.Port}/";

        var printers = new Mock<IPrintersService>();
        printers
            .Setup(s => s.GetCameraUrlsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, target));

        using var factory = new PrinterCameraProxyFactory(printers, allowedNetworkRanges: null);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/camera/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("camera_target_invalid");
        listener.ConnectionCount.Should().Be(0, "the egress guard must deny the loopback target before any socket is opened");
    }

    [Fact]
    public async Task PrintersController_LinkLocalTarget_ReturnsBadGateway_AndNeverConnects()
    {
        Guid printerId = Guid.NewGuid();
        const string target = "http://169.254.169.254/latest/meta-data/";

        var printers = new Mock<IPrintersService>();
        printers
            .Setup(s => s.GetCameraUrlsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, target));

        using var factory = new PrinterCameraProxyFactory(printers, allowedNetworkRanges: null);
        using HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/camera/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("camera_target_invalid");
    }

    [Fact]
    public async Task PrintersController_AllowlistedLoopbackTarget_ProxiesSuccessfully()
    {
        using var listener = new LoopbackHttpListener(respondSuccessfully: true);
        Guid printerId = Guid.NewGuid();
        string target = $"http://127.0.0.1:{listener.Port}/";

        var printers = new Mock<IPrintersService>();
        printers
            .Setup(s => s.GetCameraUrlsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, target));

        using var factory = new PrinterCameraProxyFactory(printers, allowedNetworkRanges: "127.0.0.1");
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/camera/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("snapshot-bytes");
        listener.ConnectionCount.Should().Be(1);
    }

    // --- Test infrastructure -------------------------------------------------------------------

    private sealed class CameraProxyFactory(Mock<ICameraService> cameras, string? allowedNetworkRanges)
        : CustomWebApplicationFactory(BuildConfig(allowedNetworkRanges))
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICameraService>();
                services.AddSingleton(cameras.Object);
            });
        }
    }

    private sealed class PrinterCameraProxyFactory(Mock<IPrintersService> printers, string? allowedNetworkRanges)
        : CustomWebApplicationFactory(BuildConfig(allowedNetworkRanges))
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrintersService>();
                services.AddSingleton(printers.Object);
            });
        }
    }

    private static Dictionary<string, string?> BuildConfig(string? allowedNetworkRanges)
    {
        var config = new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        };
        if (allowedNetworkRanges is not null)
        {
            config["ALLOWED_NETWORK_RANGES"] = allowedNetworkRanges;
        }

        return config;
    }

    /// <summary>
    /// A minimal loopback TCP listener that counts connection attempts and, optionally, replies
    /// with a valid HTTP 200 image response. Used to prove (a) the egress guard blocks a
    /// connection before it is ever opened, and (b) a guard-allowed target still round-trips a
    /// real upstream response end-to-end through the proxy.
    /// </summary>
    private sealed class LoopbackHttpListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _connectionCount;

        public LoopbackHttpListener(bool respondSuccessfully)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(respondSuccessfully, _cts.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        private async Task AcceptLoopAsync(bool respondSuccessfully, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using TcpClient tcpClient = await _listener.AcceptTcpClientAsync(ct);
                    Interlocked.Increment(ref _connectionCount);

                    if (!respondSuccessfully)
                    {
                        continue;
                    }

                    using NetworkStream stream = tcpClient.GetStream();
                    byte[] readBuffer = new byte[4096];
                    _ = await stream.ReadAsync(readBuffer, ct);

                    byte[] body = Encoding.ASCII.GetBytes("snapshot-bytes");
                    string headers =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: image/jpeg\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
                    await stream.WriteAsync(body, ct);
                    await stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
