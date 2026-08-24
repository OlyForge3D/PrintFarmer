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
    public async Task ProbeAsync_UpstreamRedirectsToInternalAddress_RedirectTargetIsNeverContacted()
    {
        const string redirectTarget = "http://192.0.2.99/internal-secret";
        int redirectTargetHits = 0;

        SpyHttpMessageHandler handler = new(req =>
        {
            if (req.RequestUri!.ToString().StartsWith(redirectTarget, StringComparison.OrdinalIgnoreCase))
            {
                redirectTargetHits++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }

            HttpResponseMessage redirect = new(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(redirectTarget);
            return redirect;
        });

        using HttpClient http = new(handler);
        SpoolmanService svc = CreateService(http, TestInfrastructure.TestHelpers.PermissiveEgressGuard());

        SpoolmanProbeResult result = await svc.ProbeAsync("http://spoolman.local", CancellationToken.None);

        result.Success.Should().BeFalse("a 302 response is not itself a successful probe");
        redirectTargetHits.Should().Be(0, "the probe must not follow a redirect to a different (potentially internal) address");
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
        SpoolmanService svc = CreateService(http, TestInfrastructure.TestHelpers.PermissiveEgressGuard());

        SpoolmanProbeResult result = await svc.ProbeAsync("http://spoolman.local:7912", CancellationToken.None);

        result.Success.Should().BeTrue("a legitimate, reachable Spoolman base URL must still probe successfully");
        result.Version.Should().Be("1.2.3");
    }

    [Fact]
    public async Task ProbeAsync_RealEgressGuard_LoopbackListener_ReceivesZeroConnections()
    {
        // Integration-style test using the production EgressGuard (no mock) against a real
        // local HTTP listener, proving loopback destinations never receive a connection.
        using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        int connectionsReceived = 0;
        CancellationTokenSource listenerCts = new();
        Task listenerTask = Task.Run(async () =>
        {
            try
            {
                while (!listenerCts.IsCancellationRequested)
                {
                    HttpListenerContext ctx = await listener.GetContextAsync();
                    Interlocked.Increment(ref connectionsReceived);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            }
            catch (Exception) when (listenerCts.IsCancellationRequested || listenerCts.Token.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });

        try
        {
            IConfiguration configuration = new ConfigurationBuilder().Build();
            EgressGuard realGuard = new(configuration, NullLogger<EgressGuard>.Instance);

            SpyHttpMessageHandler handler = new();
            using HttpClient http = new(handler);
            SpoolmanService svc = CreateService(http, realGuard);

            SpoolmanProbeResult result = await svc.ProbeAsync($"http://127.0.0.1:{port}", CancellationToken.None);

            result.Success.Should().BeFalse();
            handler.CallCount.Should().Be(0, "the real EgressGuard denies loopback destinations, so no request should reach the transport");
        }
        finally
        {
            listenerCts.Cancel();
            listener.Stop();
            listener.Close();
        }

        connectionsReceived.Should().Be(0, "a loopback probe target must receive zero connections when the egress guard denies it");
    }

    private static SpoolmanService CreateService(HttpClient http, IEgressGuard egressGuard)
    {
        Mock<ISettingsService> settings = new();
        Mock<ILogger<SpoolmanService>> logger = new();
        return new SpoolmanService(http, settings.Object, logger.Object, egressGuard);
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
}
