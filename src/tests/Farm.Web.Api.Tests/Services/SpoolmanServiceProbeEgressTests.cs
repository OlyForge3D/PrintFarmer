using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Verifies the fix for issue #1965: <c>SpoolmanService.ProbeAsync</c> must vet the
/// fully-constructed, path-bearing probe URI through <see cref="IEgressGuard"/> before making
/// any outbound call, must never follow a redirect to an internal address, and must not regress
/// legitimate probing of a reachable Spoolman instance.
/// </summary>
public class SpoolmanServiceProbeEgressTests
{
    [Fact]
    public async Task ProbeAsync_EgressGuardDenies_ReturnsUnsuccessfulAndNeverInvokesHandler()
    {
        // Asserting only on the return value would pass even if the request had been made and
        // discarded, which is precisely the bug the fix closes — so this test asserts on the
        // handler's call count, not just the result.
        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        _ = egressGuard
            .Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EgressCheckResult.Deny("Destination resolves to a loopback, link-local, or multicast address"));

        SpyHttpMessageHandler handler = new();
        using HttpClient http = new(handler);
        SpoolmanService svc = CreateService(http, egressGuard.Object);

        SpoolmanProbeResult result = await svc.ProbeAsync("http://169.254.169.254", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCategory.Should().Be("egress_denied");
        handler.CallCount.Should().Be(0, "the egress guard denied the destination, so no outbound request should ever be made");
    }

    [Fact]
    public async Task ProbeAsync_UpstreamRedirectsToInternalAddress_ProductionHandlerDoesNotFollowRedirect()
    {
        // A stub HttpMessageHandler subclass never auto-follows a 3xx response regardless of
        // AllowAutoRedirect — that setting is only honored by HttpClientHandler /
        // SocketsHttpHandler — so a test built on a stub handler would pass even without the
        // fix. This test instead uses two real loopback HTTP listeners and the exact
        // HttpClientHandler configuration (AllowAutoRedirect = false) that
        // ServiceCollectionExtensions wires onto the production SpoolmanService HttpClient: one
        // listener serves the initial 302, the other is the redirect target and must receive
        // zero connections if the fix holds.
        using RecordingLoopbackServer targetServer = RecordingLoopbackServer.Start(_ => (HttpStatusCode.OK, null));
        using RecordingLoopbackServer redirectServer = RecordingLoopbackServer.Start(_ => (HttpStatusCode.Found, targetServer.BaseUrl + "/internal-secret"));

        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            // Loopback-only real HTTP listeners (RecordingLoopbackServer) — no TLS certificate is
            // ever involved, but set this explicitly rather than defer CA5399.
            CheckCertificateRevocationList = true,
        };
        using HttpClient http = new(handler);
        // Note: TestHelpers.PermissiveEgressGuard() pins to a fixed documentation address
        // (203.0.113.100), which is fine for tests using a stubbed transport but would break a
        // real network round-trip here. AllowAnyDestinationGuard() below allows the request
        // without rewriting the address, preserving the real loopback URI being tested against.
        SpoolmanService svc = CreateService(http, AllowAnyDestinationGuard());

        SpoolmanProbeResult result = await svc.ProbeAsync(redirectServer.BaseUrl, CancellationToken.None);

        result.Success.Should().BeFalse("a 302 response is not itself a successful probe");
        redirectServer.RequestCount.Should().BeGreaterThan(0, "the redirecting server itself must have been contacted");
        targetServer.RequestCount.Should().Be(
            0,
            "AllowAutoRedirect=false must prevent the client from ever following the redirect to the (potentially internal) target server");
    }

    [Fact]
    public async Task ProbeAsync_VetsTheFullPathBearingUriActuallyRequested()
    {
        List<string> vettedUrls = [];
        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        _ = egressGuard
            .Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((url, _) => vettedUrls.Add(url))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Allow(new Uri(url), IPAddress.Parse("203.0.113.100")));

        SpyHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"version\":\"1.0.0\"}", Encoding.UTF8, "application/json")
        });
        using HttpClient http = new(handler);
        SpoolmanService svc = CreateService(http, egressGuard.Object);

        SpoolmanProbeResult result = await svc.ProbeAsync("http://spoolman.local", CancellationToken.None);

        result.Success.Should().BeTrue();
        vettedUrls.Should().Contain(
            "http://spoolman.local/api/v1/health",
            "the guard must be checked against the fully-constructed, path-bearing URI actually requested, not just the base URL");
    }

    [Fact]
    public async Task ProbeAsync_LegitimateReachableSpoolmanUrl_StillProbesSuccessfully()
    {
        SpyHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"version\":\"1.2.3\"}", Encoding.UTF8, "application/json")
        });
        using HttpClient http = new(handler);
        SpoolmanService svc = CreateService(http, AppDbTestHelpers.PermissiveEgressGuard());

        SpoolmanProbeResult result = await svc.ProbeAsync("http://spoolman.local:7912", CancellationToken.None);

        result.Success.Should().BeTrue("a legitimate, reachable Spoolman base URL must still probe successfully");
        result.Version.Should().Be("1.2.3");
    }

    [Fact]
    public async Task ProbeAsync_RealEgressGuard_LoopbackListener_ReceivesZeroConnections()
    {
        // Integration-style test using the production EgressGuard (no mock) and a
        // network-capable handler (wrapping the same AllowAutoRedirect=false HttpClientHandler
        // configuration used in production) against a real local HTTP listener. Using a spy
        // handler here would make the assertion vacuous — the listener could never be reached
        // regardless of whether the guard actually ran — because the spy is a transport dead
        // end. RecordingHandler still lets a request through to the real network if the guard
        // is ever bypassed, so "zero connections" and "handler never invoked" are both
        // genuinely tied to the guard denying the destination.
        using RecordingLoopbackServer listener = RecordingLoopbackServer.Start(_ => (HttpStatusCode.OK, null));

        IConfiguration configuration = new ConfigurationBuilder().Build();
        EgressGuard realGuard = new(configuration, NullLogger<EgressGuard>.Instance);

        using RecordingHandler handler = new();
        using HttpClient http = new(handler);
        SpoolmanService svc = CreateService(http, realGuard);

        SpoolmanProbeResult result = await svc.ProbeAsync(listener.BaseUrl, CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.CallCount.Should().Be(0, "the real EgressGuard denies loopback destinations, so no request should reach the transport");
        listener.RequestCount.Should().Be(0, "a loopback probe target must receive zero connections when the egress guard denies it");
    }

    private static SpoolmanService CreateService(HttpClient http, IEgressGuard egressGuard)
    {
        Mock<ISettingsService> settings = new();
        Mock<ILogger<SpoolmanService>> logger = new();
        return new SpoolmanService(http, settings.Object, logger.Object, egressGuard);
    }

    /// <summary>
    /// An <see cref="IEgressGuard"/> stub that allows every destination without rewriting its
    /// address (unlike <c>TestHelpers.PermissiveEgressGuard()</c>, which pins to a fixed
    /// documentation address suitable only for tests using a stubbed transport). Used by tests
    /// that make a real network round-trip against a local loopback listener, where the request
    /// must actually reach the address under test.
    /// </summary>
    private static IEgressGuard AllowAnyDestinationGuard()
    {
        Mock<IEgressGuard> mock = new(MockBehavior.Strict);
        _ = mock
            .Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => EgressCheckResult.Allow(new Uri(url), null));
        return mock.Object;
    }

    /// <summary>
    /// Records every outbound send and returns a caller-supplied response (defaulting to 404),
    /// used to prove no outbound HTTP call is made for denied requests and to simulate upstream
    /// responses (including redirects) for allowed requests.
    /// </summary>
    private sealed class SpyHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    /// <summary>
    /// Wraps the exact <see cref="HttpClientHandler"/> configuration production uses
    /// (<c>AllowAutoRedirect = false</c>) so that, unlike <see cref="SpyHttpMessageHandler"/>, a
    /// call that unexpectedly reaches the transport actually attempts a real network connection
    /// rather than silently no-oping. This keeps "handler never invoked" assertions genuinely
    /// tied to the egress guard denying the destination rather than to a stub that could never
    /// have connected regardless.
    /// </summary>
    private sealed class RecordingHandler() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A minimal real HTTP server bound to loopback, used to prove behavior over an actual
    /// network round-trip (rather than a stubbed <see cref="HttpMessageHandler"/>) — required to
    /// meaningfully test redirect-following, since a stub handler never auto-follows redirects
    /// regardless of the fix under test.
    /// </summary>
    private sealed class RecordingLoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<HttpListenerRequest, (HttpStatusCode Status, string? Location)> _responder;
        private int _requestCount;

        private RecordingLoopbackServer(HttpListener listener, string baseUrl, Func<HttpListenerRequest, (HttpStatusCode Status, string? Location)> responder)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _responder = responder;
            _ = AcceptLoopAsync();
        }

        public string BaseUrl { get; }

        public int RequestCount => _requestCount;

        public static RecordingLoopbackServer Start(Func<HttpListenerRequest, (HttpStatusCode Status, string? Location)> responder)
        {
            using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            string baseUrl = $"http://127.0.0.1:{port}";
            HttpListener listener = new();
            listener.Prefixes.Add(baseUrl + "/");
            listener.Start();
            return new RecordingLoopbackServer(listener, baseUrl, responder);
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

                Interlocked.Increment(ref _requestCount);
                (HttpStatusCode status, string? location) = _responder(context.Request);
                context.Response.StatusCode = (int)status;
                if (location is not null)
                {
                    context.Response.RedirectLocation = location;
                }

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
