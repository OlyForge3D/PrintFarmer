using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
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
        private readonly SpyHttpMessageHandler _handler;

        public TestConnectionTestFactory(SpyHttpMessageHandler handler)
            : base(BuildConfigOverrides())
        {
            _handler = handler;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            SpyHttpMessageHandler handler = _handler;
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("VettedEgress")
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        }

        private static Dictionary<string, string?> BuildConfigOverrides() =>
            new()
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            };
    }
}
