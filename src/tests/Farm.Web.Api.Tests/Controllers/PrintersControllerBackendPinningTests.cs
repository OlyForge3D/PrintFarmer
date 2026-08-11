using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Extends the pinning proof in <see cref="PrintersControllerTestConnectionTests"/> (which only
/// covers Moonraker) to the remaining four backends dispatched by
/// <see cref="PrintersController.TestConnectionAsync"/>: OctoPrint, PrusaLink, SDCP, and
/// FlashForge. Each test proves the backend actually dials the egress guard's vetted IP address
/// rather than re-resolving the original hostname, closing the gap flagged in the #1430 review
/// where only Moonraker had a pinned-target assertion.
/// </summary>
public class PrintersControllerBackendPinningTests
{
    [Fact]
    public async Task OctoPrint_WhenEgressGuardResolvesAnAddress_ConnectionTargetsThePinnedIpNotTheHostname()
    {
        HttpRequestMessage? capturedRequest = null;
        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new RecordingHandler(request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"api\":{\"version\":\"1.0\"}}", Encoding.UTF8, "application/json")
                };
            })));

        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        egressGuard
            .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Allow(new Uri(url), IPAddress.Parse("203.0.113.6")));

        PrintersController controller = CreateControllerWithFactory(httpClientFactory.Object, Mock.Of<IBackendClientFactory>(), egressGuard.Object);

        var request = new TestConnectionRequest(
            ServerUrl: "http://octoprint.local:80/",
            Backend: PrinterBackend.OctoPrint,
            ApiKey: "test-api-key");

        ActionResult<TestConnectionResponse> result = await controller.TestConnectionAsync(request, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        TestConnectionResponse response = Assert.IsType<TestConnectionResponse>(okResult.Value);
        response.Success.Should().BeTrue();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.Host.Should().Be("203.0.113.6", "OctoPrint must dial the vetted IP, not re-resolve the hostname");
        capturedRequest.Headers.Host.Should().Be("octoprint.local", "the original hostname must be preserved via the Host header for virtual-hosting/SNI");
    }

    [Fact]
    public async Task PrusaLink_WhenEgressGuardResolvesAnAddress_ConnectionTargetsThePinnedIpNotTheHostname()
    {
        using LoopbackDigestServer server = LoopbackDigestServer.Start("prusalink.invalid.test");

        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        egressGuard
            .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Allow(new Uri(url), IPAddress.Loopback));

        // A non-resolvable placeholder hostname: if pinning were not applied, PrusaLink would
        // attempt to connect to this literal hostname and fail with a DNS resolution error
        // rather than reaching the loopback server below.
        PrintersController controller = CreateController(
            new HttpClient(),
            Mock.Of<IBackendClientFactory>(),
            egressGuard.Object);

        var request = new TestConnectionRequest(
            ServerUrl: $"http://prusalink.invalid.test:{server.Port}/",
            Backend: PrinterBackend.PrusaLink,
            Password: "secret");

        ActionResult<TestConnectionResponse> result = await controller.TestConnectionAsync(request, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        TestConnectionResponse response = Assert.IsType<TestConnectionResponse>(okResult.Value);
        response.Success.Should().BeTrue(
            $"a successful connection proves the request actually reached the loopback server at the pinned IP " +
            $"rather than failing DNS resolution against the non-resolvable placeholder hostname (actual message: {response.Message})");

        server.LastHostHeader.Should().StartWith(
            "prusalink.invalid.test",
            "the original hostname must be preserved via the Host header for virtual-hosting/SNI");
    }

    [Theory]
    [InlineData(PrinterBackend.SDCP)]
    [InlineData(PrinterBackend.FlashForge)]
    public async Task NonHttpBackend_WhenEgressGuardResolvesAnAddress_ConnectionTargetsThePinnedIpNotTheHostname(PrinterBackend backend)
    {
        Uri? capturedUri = null;
        var connectionTestClient = new Mock<IBackendClient>().As<ISupportsConnectionTest>();
        connectionTestClient
            .Setup(c => c.TestConnectionAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Callback<Uri, CancellationToken>((uri, _) => capturedUri = uri)
            .ReturnsAsync(true);

        var backendClientFactory = new Mock<IBackendClientFactory>();
        backendClientFactory.Setup(f => f.GetClient(backend)).Returns((IBackendClient)connectionTestClient.Object);

        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        egressGuard
            .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Allow(new Uri(url), IPAddress.Parse("203.0.113.9")));

        PrintersController controller = CreateController(new HttpClient(), backendClientFactory.Object, egressGuard.Object);

        var request = new TestConnectionRequest(
            ServerUrl: "http://printer.local:8080/",
            Backend: backend);

        ActionResult<TestConnectionResponse> result = await controller.TestConnectionAsync(request, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        TestConnectionResponse response = Assert.IsType<TestConnectionResponse>(okResult.Value);
        response.Success.Should().BeTrue();

        capturedUri.Should().NotBeNull();
        capturedUri!.Host.Should().Be("203.0.113.9", $"{backend} must dial the vetted IP, not re-resolve the hostname");
    }

    private static PrintersController CreateController(
        HttpClient httpClient,
        IBackendClientFactory backendClientFactory,
        IEgressGuard egressGuard)
    {
        Mock<IHttpClientFactory> httpClientFactory = new();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        return CreateControllerWithFactory(httpClientFactory.Object, backendClientFactory, egressGuard);
    }

    private static PrintersController CreateControllerWithFactory(
        IHttpClientFactory httpClientFactory,
        IBackendClientFactory backendClientFactory,
        IEgressGuard egressGuard)
    {
        var controller = new PrintersController(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: Mock.Of<IPrintersService>(),
            catalogService: Mock.Of<Farm.Web.Api.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            discoverySessions: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoverySessionRegistry>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: backendClientFactory,
            httpClientFactory: httpClientFactory,
            egressGuard: egressGuard,
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: Mock.Of<Farm.Infrastructure.Telemetry.IPrintFarmerTelemetryService>(),
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                ], "test")),
            },
        };
        return controller;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    /// <summary>
    /// A minimal real HTTP/1.1 loopback server, used to prove PrusaLink's dedicated
    /// <c>DigestAuthHandler</c> connection (which does not go through the
    /// <c>IHttpClientFactory</c>-provided "VettedEgress" client) also targets the pinned IP
    /// rather than the original hostname. A real TCP connection is required here because
    /// <see cref="Farm.Backend.Plugin.Core.DigestAuthHandler"/> hard-codes its own
    /// <c>HttpClientHandler</c> that cannot be substituted with a fake in-process handler.
    /// Responds 200 unconditionally (no digest challenge/retry round trip) so the test proves
    /// only what it needs to — that the connection is reachable at the pinned loopback address
    /// with the correct Host header — without depending on the two-round-trip Digest handshake
    /// timing, which proved flaky on some CI runners.
    /// </summary>
    private sealed class LoopbackDigestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        private LoopbackDigestServer(HttpListener listener, int port)
        {
            _listener = listener;
            Port = port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public string? LastHostHeader { get; private set; }

        public static LoopbackDigestServer Start(string? additionalHostPrefix = null)
        {
            int port = GetFreeLoopbackPort();
            HttpListener listener = new();

            // HttpListener routes incoming requests to a registered prefix by matching the
            // request's Host header, not just the socket's bound address. The test client sends
            // a Host header of the original (non-loopback) hostname to prove it's preserved
            // end-to-end, and the two platform implementations diverge on how to accept that:
            //  - Windows' native http.sys backing HttpListener is lenient here and accepts any
            //    Host header against a single "http://127.0.0.1:{port}/" prefix, but registering
            //    a "+"/"*" (all-hosts) prefix requires an admin URL ACL reservation and fails
            //    with "Access is denied" for a normal dev/CI user.
            //  - The managed cross-platform HttpListener implementation used on Linux enforces an
            //    exact Host match against registered prefixes (no admin concept applies to "+"
            //    there, since it is just a plain socket bind), so a plain loopback prefix 404s
            //    any request whose Host header doesn't literally match "127.0.0.1".
            listener.Prefixes.Add(
                additionalHostPrefix is not null && !OperatingSystem.IsWindows()
                    ? $"http://+:{port}/"
                    : $"http://127.0.0.1:{port}/");
            listener.Start();
            return new LoopbackDigestServer(listener, port);
        }

        private static int GetFreeLoopbackPort()
        {
            using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                LastHostHeader = context.Request.Headers["Host"];
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }
    }
}
