using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Domain;
using Farm.Modules.Printers.Controllers.Requests;
using Farm.Modules.PrintQueue.Controllers.Responses;
using Farm.Web.Api.Tests;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// HTTP-layer tests for the authorization gate and egress vetting on
/// <c>POST /api/printers/test-connection</c> (issue #1422). Verifies that:
/// - an approved non-admin user is denied (403), with no outbound HTTP call;
/// - a farm_admin caller is vetted through <c>IEgressGuard</c> before any outbound call, and a
///   denied destination never reaches the network;
/// - the vetted client used for the OctoPrint/Moonraker probe path does not follow redirects to
///   internal addresses.
/// </summary>
public sealed class PrintersControllerTestConnectionAuthorizationTests : IAsyncLifetime
{
    private readonly SpyHttpMessageHandler _spyHandler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
    });

    private TestConnectionTestFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new TestConnectionTestFactory(_spyHandler);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task NonAdminUser_TestConnection_ReturnsForbiddenWithoutOutboundCall()
    {
        using HttpClient client = CreateOperatorClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/printers/test-connection",
            new TestConnectionRequest("http://192.168.1.50:80", PrinterBackend.OctoPrint, ApiKey: "key"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _spyHandler.CallCount.Should().Be(0, "no outbound HTTP call should be made when authorization is denied");
    }

    [Fact]
    public async Task FarmAdmin_LoopbackDestination_RejectedWithoutOutboundCall()
    {
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/printers/test-connection",
            new TestConnectionRequest("http://127.0.0.1:80", PrinterBackend.OctoPrint, ApiKey: "key"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        TestConnectionResponse? body = await response.Content.ReadFromJsonAsync<TestConnectionResponse>();
        body.Should().NotBeNull();
        body!.Success.Should().BeFalse();
        body.Message.Should().Be("The requested server address is not allowed.");
        body.Message.Should().NotContain("loopback", "the denial reason must not be echoed to the caller (oracle removal)");
        _spyHandler.CallCount.Should().Be(0, "no outbound HTTP call should be made once the egress guard denies the destination");
    }

    [Fact]
    public async Task FarmAdmin_ValidDestination_ProbesAndSucceeds()
    {
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/printers/test-connection",
            new TestConnectionRequest("http://192.168.1.50:80", PrinterBackend.OctoPrint, ApiKey: "key"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        TestConnectionResponse? body = await response.Content.ReadFromJsonAsync<TestConnectionResponse>();
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        _spyHandler.CallCount.Should().BeGreaterThan(0, "an allowed destination should be probed");
    }

    [Fact]
    public async Task FarmAdmin_UpstreamRedirectsToInternalAddress_RedirectIsNotFollowed()
    {
        SpyHttpMessageHandler redirectHandler = new(_ =>
        {
            HttpResponseMessage redirect = new(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://127.0.0.1/admin-secret");
            return redirect;
        });
        TestConnectionTestFactory redirectFactory = new(redirectHandler);
        using HttpClient client = redirectFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/printers/test-connection",
            new TestConnectionRequest("http://192.168.1.60:80", PrinterBackend.OctoPrint, ApiKey: "key"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        TestConnectionResponse? body = await response.Content.ReadFromJsonAsync<TestConnectionResponse>();
        body.Should().NotBeNull();
        body!.Success.Should().BeFalse("the redirect response itself is not a success status");
        redirectHandler.CallCount.Should().Be(
            1,
            "the vetted client must not automatically follow a redirect to an internal address");

        await redirectFactory.DisposeAsync();
    }

    [Fact]
    public async Task FarmAdmin_UpstreamRedirectsToInternalAddress_ProductionHandlerDoesNotFollowRedirect()
    {
        // Unlike FarmAdmin_UpstreamRedirectsToInternalAddress_RedirectIsNotFollowed above (which
        // swaps in a spy as the primary handler and therefore never exercises the production
        // AllowAutoRedirect=false HttpClientHandler configured in ServiceCollectionExtensions),
        // this test leaves the "VettedEgress" named HttpClient registration untouched and serves
        // the 302 from a real local HTTP listener, so the *actual* production handler processes
        // the response.
        using LoopbackRedirectServer redirectServer = LoopbackRedirectServer.Start(
            redirectTarget: "http://127.0.0.1:1/admin-secret");

        // The loopback destination is only reachable in this test because it is explicitly
        // allow-listed; the redirect target (a different loopback port) is NOT allow-listed and
        // must never be contacted, proving both that the guard vets the initial destination and
        // that the real handler refuses to auto-follow the redirect.
        TestConnectionTestFactory factory = new(allowedRanges: "127.0.0.1/32");
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/printers/test-connection",
            new TestConnectionRequest(redirectServer.BaseUrl, PrinterBackend.OctoPrint, ApiKey: "key"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        TestConnectionResponse? body = await response.Content.ReadFromJsonAsync<TestConnectionResponse>();
        body.Should().NotBeNull();
        body!.Success.Should().BeFalse("the redirect response itself is not a success status");
        redirectServer.RequestCount.Should().Be(
            1,
            "the production VettedEgress HttpClientHandler (AllowAutoRedirect=false) must not follow the redirect");

        await factory.DisposeAsync();
    }

    [Fact]
    public async Task FarmAdmin_PrusaLink_UpstreamRedirectsToInternalAddress_RedirectIsNotFollowed()
    {
        // PrusaLink does not go through the shared "VettedEgress" named HttpClient at all — it
        // builds its own HttpClient wrapping DigestAuthHandler. This proves that hardening
        // (DigestAuthHandler constructed with an inner HttpClientHandler { AllowAutoRedirect =
        // false }) actually takes effect end-to-end, rather than only by code inspection.
        using LoopbackRedirectServer redirectServer = LoopbackRedirectServer.Start(
            redirectTarget: "http://127.0.0.1:1/admin-secret");

        TestConnectionTestFactory factory = new(allowedRanges: "127.0.0.1/32");
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/printers/test-connection",
            new TestConnectionRequest(redirectServer.BaseUrl, PrinterBackend.PrusaLink, ApiKey: "maker-key"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        TestConnectionResponse? body = await response.Content.ReadFromJsonAsync<TestConnectionResponse>();
        body.Should().NotBeNull();
        body!.Success.Should().BeFalse("the redirect response itself is not a success status");
        redirectServer.RequestCount.Should().Be(
            1,
            "PrusaLink's DigestAuthHandler must be constructed with AllowAutoRedirect=false so it never follows the redirect");

        await factory.DisposeAsync();
    }

    private HttpClient CreateOperatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        return client;
    }

    private HttpClient CreateAdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");
        return client;
    }

    /// <summary>
    /// Records every outbound send and returns a caller-supplied response, used to prove no
    /// outbound HTTP call is made for denied requests and to simulate upstream responses
    /// (including redirects) for allowed requests.
    /// </summary>
    private sealed class SpyHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class TestConnectionTestFactory : CustomWebApplicationFactory
    {
        private readonly SpyHttpMessageHandler? _handler;

        public TestConnectionTestFactory(SpyHttpMessageHandler handler)
            : base(BuildConfigOverrides(null))
        {
            _handler = handler;
        }

        /// <summary>
        /// Leaves the production "VettedEgress" HttpClient registration untouched (real
        /// <see cref="HttpClientHandler"/> with AllowAutoRedirect=false), for tests that need to
        /// prove the actual production wiring rather than a substituted spy handler.
        /// </summary>
        public TestConnectionTestFactory(string? allowedRanges = null)
            : base(BuildConfigOverrides(allowedRanges))
        {
            _handler = null;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (_handler is not null)
            {
                SpyHttpMessageHandler handler = _handler;
                builder.ConfigureServices(services =>
                {
                    services.AddHttpClient("VettedEgress")
                        .ConfigurePrimaryHttpMessageHandler(() => handler);
                });
            }
        }

        private static Dictionary<string, string?> BuildConfigOverrides(string? allowedRanges)
        {
            Dictionary<string, string?> overrides = new()
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            };
            if (allowedRanges is not null)
            {
                overrides["ALLOWED_NETWORK_RANGES"] = allowedRanges;
            }

            return overrides;
        }
    }

    /// <summary>
    /// A minimal real HTTP server bound to loopback that always responds 302 to a caller-supplied
    /// redirect target, used to prove that the production VettedEgress HttpClientHandler and the
    /// PrusaLink DigestAuthHandler (both AllowAutoRedirect=false) are actually wired in — as
    /// opposed to a substituted test handler.
    /// </summary>
    private sealed class LoopbackRedirectServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _redirectTarget;
        private readonly CancellationTokenSource _cts = new();
        private int _requestCount;

        private LoopbackRedirectServer(HttpListener listener, string baseUrl, string redirectTarget)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _redirectTarget = redirectTarget;
            _ = AcceptLoopAsync();
        }

        public string BaseUrl { get; }

        public int RequestCount => _requestCount;

        public static LoopbackRedirectServer Start(string redirectTarget)
        {
            int port = GetFreeLoopbackPort();
            string baseUrl = $"http://127.0.0.1:{port}/";
            HttpListener listener = new();
            listener.Prefixes.Add(baseUrl);
            listener.Start();
            return new LoopbackRedirectServer(listener, baseUrl.TrimEnd('/'), redirectTarget);
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

                Interlocked.Increment(ref _requestCount);
                context.Response.StatusCode = (int)HttpStatusCode.Found;
                context.Response.RedirectLocation = _redirectTarget;
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
