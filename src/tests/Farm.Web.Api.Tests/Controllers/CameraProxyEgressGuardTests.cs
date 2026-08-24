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
        using var listener = new LoopbackHttpListener(IPAddress.Loopback, respondSuccessfully: true);
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
    public async Task CamerasController_PrivateLanTarget_ProxiesSuccessfully_WithDefaultGuardConfig()
    {
        // The "legitimate public/LAN target still proxies successfully" case from the issue's
        // verification plan. This deliberately uses NO ALLOWED_NETWORK_RANGES override: the
        // listener is bound to this host's own outbound LAN/private address (never loopback,
        // link-local, or multicast), so the *default* EgressGuard configuration must allow it.
        // EgressGuard intentionally does not block RFC1918/private ranges (printers/cameras live
        // on the LAN) - this test proves that default behavior, not an allowlist escape hatch.
        if (!LoopbackHttpListener.TryGetOutboundLanAddress(out IPAddress? lanAddress))
        {
            return; // no routable non-loopback interface in this sandbox; nothing to prove here.
        }

        using var listener = new LoopbackHttpListener(lanAddress, respondSuccessfully: true);
        Guid cameraId = Guid.NewGuid();
        string target = $"http://{lanAddress}:{listener.Port}/";

        var cameras = new Mock<ICameraService>();
        cameras
            .Setup(s => s.FindByIdAsync(cameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Camera
            {
                Id = cameraId,
                Name = "LAN camera",
                PrinterId = null,
                IsEnabled = true,
                StreamUrl = target,
                SnapshotUrl = target,
            });

        using var factory = new CameraProxyFactory(cameras, allowedNetworkRanges: null);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/{cameraId}/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("snapshot-bytes");
        listener.ConnectionCount.Should().Be(1);
    }

    [Fact]
    public async Task CamerasController_AllowlistedHostnameTarget_PinsConnectionAndPreservesHostHeader()
    {
        // Verifies step 2 of the fix directly: the outbound connection is pinned to the
        // egress-guard-resolved IP address (EgressGuard.CreatePinnedUri), while the ORIGINAL
        // hostname is preserved on the "Host" header, mirroring
        // ObicoServerController.ValidateServerConnectivityAsync / PrintersController:334,391-394.
        // "localhost" resolves to loopback, which is denied by default, so it is explicitly
        // allowlisted here purely to exercise the hostname -> pinned-IP rewrite; the "no
        // allowlist needed" default-allow path is covered separately by the LAN test above.
        // EgressGuard pins to the FIRST address DNS returns for the host (may be the IPv4 or
        // IPv6 loopback form depending on OS resolver order), so this test resolves "localhost"
        // itself first and binds the listener to that exact address, keeping it in lockstep with
        // whatever EgressGuard.CheckAsync will pin to.
        IPAddress[] localhostAddresses = await Dns.GetHostAddressesAsync("localhost");
        IPAddress resolvedLoopback = localhostAddresses[0];
        // EgressGuard denies if ANY resolved address is an unvetted loopback address, and
        // "localhost" commonly resolves to BOTH ::1 and 127.0.0.1 - so every address DNS
        // returns must be allowlisted, not just the one the connection will pin to.
        string allowedRanges = string.Join(',', localhostAddresses.Select(a => a.ToString()));

        using var listener = new LoopbackHttpListener(resolvedLoopback, respondSuccessfully: true);
        Guid cameraId = Guid.NewGuid();
        string target = $"http://localhost:{listener.Port}/";

        var cameras = new Mock<ICameraService>();
        cameras
            .Setup(s => s.FindByIdAsync(cameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Camera
            {
                Id = cameraId,
                Name = "Allowlisted hostname camera",
                PrinterId = null,
                IsEnabled = true,
                StreamUrl = target,
                SnapshotUrl = target,
            });

        using var factory = new CameraProxyFactory(cameras, allowedNetworkRanges: allowedRanges);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/{cameraId}/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("snapshot-bytes");
        listener.ConnectionCount.Should().Be(1);
        listener.LastRequestHost.Should().Be(
            $"localhost:{listener.Port}",
            "the proxy must connect to the pinned IP but keep sending the ORIGINAL hostname as the Host header");
    }

    // --- PrintersController: printer-attached camera proxy ------------------------------------

    [Fact]
    public async Task PrintersController_LoopbackTarget_ReturnsBadGateway_AndNeverConnects()
    {
        using var listener = new LoopbackHttpListener(IPAddress.Loopback, respondSuccessfully: true);
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
    public async Task PrintersController_PrivateLanTarget_ProxiesSuccessfully_WithDefaultGuardConfig()
    {
        // See CamerasController_PrivateLanTarget_ProxiesSuccessfully_WithDefaultGuardConfig for
        // rationale: proves the DEFAULT guard configuration (no allowlist) still proxies a
        // legitimate LAN target, since EgressGuard intentionally does not block RFC1918 ranges.
        if (!LoopbackHttpListener.TryGetOutboundLanAddress(out IPAddress? lanAddress))
        {
            return; // no routable non-loopback interface in this sandbox; nothing to prove here.
        }

        using var listener = new LoopbackHttpListener(lanAddress, respondSuccessfully: true);
        Guid printerId = Guid.NewGuid();
        string target = $"http://{lanAddress}:{listener.Port}/";

        var printers = new Mock<IPrintersService>();
        printers
            .Setup(s => s.GetCameraUrlsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, target));

        using var factory = new PrinterCameraProxyFactory(printers, allowedNetworkRanges: null);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/camera/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("snapshot-bytes");
        listener.ConnectionCount.Should().Be(1);
    }

    [Fact]
    public async Task PrintersController_AllowlistedHostnameTarget_PinsConnectionAndPreservesHostHeader()
    {
        // See CamerasController_AllowlistedHostnameTarget_PinsConnectionAndPreservesHostHeader
        // for rationale: verifies the pinned-IP connect + original-Host-header behavior, and why
        // every address DNS resolves "localhost" to (both ::1 and 127.0.0.1 on most hosts) must
        // be allowlisted, not just the one the connection will pin to.
        IPAddress[] localhostAddresses = await Dns.GetHostAddressesAsync("localhost");
        IPAddress resolvedLoopback = localhostAddresses[0];
        string allowedRanges = string.Join(',', localhostAddresses.Select(a => a.ToString()));

        using var listener = new LoopbackHttpListener(resolvedLoopback, respondSuccessfully: true);
        Guid printerId = Guid.NewGuid();
        string target = $"http://localhost:{listener.Port}/";

        var printers = new Mock<IPrintersService>();
        printers
            .Setup(s => s.GetCameraUrlsForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, target));

        using var factory = new PrinterCameraProxyFactory(printers, allowedNetworkRanges: allowedRanges);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/camera/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("snapshot-bytes");
        listener.ConnectionCount.Should().Be(1);
        listener.LastRequestHost.Should().Be(
            $"localhost:{listener.Port}",
            "the proxy must connect to the pinned IP but keep sending the ORIGINAL hostname as the Host header");
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
    /// A minimal TCP listener that counts connection attempts, captures the request's "Host"
    /// header, and optionally replies with a valid HTTP 200 image response. Used to prove
    /// (a) the egress guard blocks a connection before it is ever opened, (b) a guard-allowed
    /// target still round-trips a real upstream response end-to-end through the proxy, and
    /// (c) the proxy connects to the pinned IP while preserving the original hostname on the
    /// "Host" header (issue #1964, fix step 2).
    /// </summary>
    private sealed class LoopbackHttpListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _connectionCount;
        private string? _lastRequestHost;

        public LoopbackHttpListener(IPAddress bindAddress, bool respondSuccessfully)
        {
            _listener = new TcpListener(bindAddress, 0);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(respondSuccessfully, _cts.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        /// <summary>The value of the "Host" header on the most recently received request, if any.</summary>
        public string? LastRequestHost => Volatile.Read(ref _lastRequestHost);

        /// <summary>
        /// Finds an address this host would actually use to reach the wider network (never
        /// loopback/link-local/multicast), suitable for proving that the DEFAULT egress-guard
        /// configuration allows ordinary LAN/private targets. Uses a UDP "connect" purely to ask
        /// the OS for its outbound route; no packet is transmitted, so this is safe without real
        /// network connectivity. Returns false if no such interface is available (e.g. an
        /// isolated sandbox with only loopback), in which case the caller should skip.
        /// </summary>
        public static bool TryGetOutboundLanAddress([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("8.8.8.8", 65530);
                var localEndPoint = socket.LocalEndPoint as IPEndPoint;
                IPAddress? candidate = localEndPoint?.Address;
                if (candidate is not null
                    && !IPAddress.IsLoopback(candidate)
                    && candidate.AddressFamily == AddressFamily.InterNetwork)
                {
                    address = candidate;
                    return true;
                }
            }
            catch (SocketException)
            {
            }

            address = null;
            return false;
        }

        private async Task AcceptLoopAsync(bool respondSuccessfully, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using TcpClient tcpClient = await _listener.AcceptTcpClientAsync(ct);
                    Interlocked.Increment(ref _connectionCount);

                    using NetworkStream stream = tcpClient.GetStream();
                    string requestText = await ReadHttpRequestHeadersAsync(stream, ct);
                    Volatile.Write(ref _lastRequestHost, ParseHostHeader(requestText));

                    if (!respondSuccessfully)
                    {
                        continue;
                    }

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

        private static async Task<string> ReadHttpRequestHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var builder = new StringBuilder();
            while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return builder.ToString();
        }

        private static string? ParseHostHeader(string requestText)
        {
            foreach (string line in requestText.Split("\r\n"))
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    return line["Host:".Length..].Trim();
                }
            }

            return null;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
