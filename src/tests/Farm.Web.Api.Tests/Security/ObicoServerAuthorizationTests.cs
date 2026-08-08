using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// HTTP-layer tests for the administrative authorization gate on <c>/api/obico-servers</c> and
/// for the egress vetting applied to the Obico connectivity probe. Verifies that:
/// - an approved non-admin user is denied (403) on every verb, with no DB mutation and no
///   outbound HTTP call;
/// - a farm_admin user (holding <see cref="PrintFarmerPermissions.Integrations.ManageObico"/>)
///   passes authorization;
/// - the egress guard rejects loopback/link-local destinations without an outbound call, allows
///   a destination in a configured allowed range, and the vetted client does not follow redirects
///   to internal addresses.
/// </summary>
public sealed class ObicoServerAuthorizationTests : IAsyncLifetime
{
    private readonly SpyHttpMessageHandler _spyHandler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"detections\":[]}", System.Text.Encoding.UTF8, "application/json")
    });

    private ObicoTestFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ObicoTestFactory(_spyHandler);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("GET", "/api/obico-servers")]
    [InlineData("POST", "/api/obico-servers")]
    public async Task NonAdminUser_CollectionRoute_ReturnsForbiddenWithoutOutboundCall(string method, string route)
    {
        using HttpClient client = CreateOperatorClient();
        using HttpRequestMessage request = CreateRequest(method, route);

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _spyHandler.CallCount.Should().Be(0, "no outbound HTTP call should be made when authorization is denied");
        await AssertNoObicoServersPersistedAsync();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task NonAdminUser_ItemRoute_ReturnsForbiddenWithoutOutboundCall(string method)
    {
        Guid serverId = await SeedObicoServerAsync();
        using HttpClient client = CreateOperatorClient();
        using HttpRequestMessage request = CreateRequest(method, $"/api/obico-servers/{serverId}");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _spyHandler.CallCount.Should().Be(0, "no outbound HTTP call should be made when authorization is denied");

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ObicoServer? server = await dbContext.ObicoServers.FindAsync(serverId);
        server.Should().NotBeNull();
        server!.Name.Should().Be("Seeded Obico");
    }

    [Fact]
    public async Task NonAdminUser_HealthRoute_ReturnsForbiddenWithoutOutboundCall()
    {
        Guid serverId = await SeedObicoServerAsync();
        using HttpClient client = CreateOperatorClient();
        using HttpRequestMessage request = CreateRequest("GET", $"/api/obico-servers/{serverId}/health");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _spyHandler.CallCount.Should().Be(0, "no outbound HTTP call should be made when authorization is denied");
    }

    [Fact]
    public async Task FarmAdmin_CreateThenReadThenDelete_Succeeds()
    {
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/obico-servers",
            new { Name = "Admin Obico", Url = "http://192.168.1.50:3333", IsEnabled = true });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        _spyHandler.CallCount.Should().BeGreaterThan(0, "the admin-owned request should probe the configured server");

        HttpResponseMessage listResponse = await client.GetAsync("/api/obico-servers");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ObicoServer created = await dbContext.ObicoServers.SingleAsync(s => s.Name == "Admin Obico");

        HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/obico-servers/{created.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task FarmAdmin_CreateWithLoopbackDestination_RejectedWithoutOutboundCall()
    {
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/obico-servers",
            new { Name = "Loopback Obico", Url = "http://127.0.0.1:3333", IsEnabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("loopback");
        _spyHandler.CallCount.Should().Be(0, "no outbound HTTP call should be made once the egress guard denies the destination");
    }

    [Fact]
    public async Task FarmAdmin_CreateWithAllowedLoopbackRange_StillProbes()
    {
        ObicoTestFactory allowRangeFactory = new(_spyHandler, allowedRanges: "127.0.0.1/32");
        using HttpClient client = allowRangeFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/obico-servers",
            new { Name = "Allowed Loopback Obico", Url = "http://127.0.0.1:3333", IsEnabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        _spyHandler.CallCount.Should().BeGreaterThan(0, "an explicitly allowed loopback destination should still be probed");

        await allowRangeFactory.DisposeAsync();
    }

    [Fact]
    public async Task FarmAdmin_UpstreamRedirectsToInternalAddress_RedirectIsNotFollowed()
    {
        SpyHttpMessageHandler redirectHandler = new(request =>
        {
            HttpResponseMessage redirect = new(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://127.0.0.1/admin-secret");
            return redirect;
        });
        ObicoTestFactory redirectFactory = new(redirectHandler);
        using HttpClient client = redirectFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/obico-servers",
            new { Name = "Redirecting Obico", Url = "http://192.168.1.60:3333", IsEnabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("HTTP 302");
        redirectHandler.CallCount.Should().Be(
            1,
            "the vetted client must not automatically follow a redirect to an internal address");

        await redirectFactory.DisposeAsync();
    }

    [Fact]
    public async Task FarmAdmin_UpstreamRedirectsToInternalAddress_ProductionHandlerDoesNotFollowRedirect()
    {
        // Unlike FarmAdmin_UpstreamRedirectsToInternalAddress_RedirectIsNotFollowed above (which
        // swaps in a spy as the primary handler and therefore never actually exercises the
        // production AllowAutoRedirect=false HttpClientHandler configured in
        // ServiceCollectionExtensions), this test leaves the "VettedEgress" named HttpClient
        // registration untouched and instead serves the 302 from a real local HTTP listener, so
        // the *actual* production handler processes the response.
        using LoopbackRedirectServer redirectServer = LoopbackRedirectServer.Start(
            redirectTarget: "http://127.0.0.1:1/admin-secret");

        // The loopback destination is only reachable in this test because it is explicitly
        // allow-listed; the redirect target (a different loopback port) is NOT allow-listed and
        // must never be contacted, proving both that the guard vets the initial destination and
        // that the real handler refuses to auto-follow the redirect.
        ObicoTestFactory factory = new(allowedRanges: $"127.0.0.1/32");
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/obico-servers",
            new { Name = "Real Redirecting Obico", Url = redirectServer.BaseUrl, IsEnabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("HTTP 302");
        redirectServer.RequestCount.Should().Be(
            1,
            "the production VettedEgress HttpClientHandler (AllowAutoRedirect=false) must not follow the redirect");

        await factory.DisposeAsync();
    }

    private async Task<Guid> SeedObicoServerAsync()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Seeded Obico",
            Url = "http://192.168.1.50:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();
        return server.Id;
    }

    private async Task AssertNoObicoServersPersistedAsync()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await dbContext.ObicoServers.CountAsync()).Should().Be(0);
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

    private static HttpRequestMessage CreateRequest(string method, string route)
    {
        HttpMethod httpMethod = new(method);
        HttpRequestMessage request = new(httpMethod, route);
        if (httpMethod != HttpMethod.Get && httpMethod != HttpMethod.Delete)
        {
            request.Content = JsonContent.Create(new { Name = "Ignored", Url = "http://192.168.1.50:3333" });
        }

        return request;
    }

    /// <summary>
    /// Records every outbound send and returns a caller-supplied response, used to prove no
    /// outbound HTTP call is made for denied requests and to simulate upstream Obico responses
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

    private sealed class ObicoTestFactory : CustomWebApplicationFactory
    {
        private readonly SpyHttpMessageHandler? _handler;

        public ObicoTestFactory(SpyHttpMessageHandler handler, string? allowedRanges = null)
            : base(BuildConfigOverrides(allowedRanges))
        {
            _handler = handler;
        }

        /// <summary>
        /// Leaves the production "VettedEgress" HttpClient registration untouched (real
        /// <see cref="HttpClientHandler"/> with AllowAutoRedirect=false), for tests that need to
        /// prove the actual production wiring rather than a substituted spy handler.
        /// </summary>
        public ObicoTestFactory(string? allowedRanges = null)
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
    /// redirect target, used to prove that the production VettedEgress HttpClientHandler
    /// (AllowAutoRedirect=false) is actually wired in — as opposed to a substituted test handler.
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
