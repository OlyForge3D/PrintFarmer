using System.Net;
using System.Net.Http.Json;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Monitoring;

[Collection(IntegrationTestCollection.Name)]
public class MonitoringControllerTests : IAsyncLifetime
{
    private CustomWebApplicationFactory? _factory;
    private HttpClient? _adminClient;
    private HttpClient? _anonClient;

    public async Task InitializeAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _anonClient = _factory.CreateClient();
        await _factory.ResetDatabaseAsync();
        _adminClient = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateSession_AsAdmin_ReturnsSuccessWithCookie()
    {
        var response = await _adminClient!.PostAsync("/api/monitoring/session", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SessionResponse>();
        body!.Success.Should().BeTrue();
        body.ExpiresAt.Should().NotBeNullOrEmpty();

        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies.Should().Contain(c => c.StartsWith("pf_monitoring_session="));
    }

    [Fact]
    public async Task CreateSession_Unauthenticated_Returns401()
    {
        var response = await _anonClient!.PostAsync("/api/monitoring/session", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task VerifySession_NoCookie_Returns401()
    {
        var response = await _anonClient!.GetAsync("/api/monitoring/verify");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task VerifySession_WithValidCookie_Returns200()
    {
        // Create session to get cookie
        var sessionResponse = await _adminClient!.PostAsync("/api/monitoring/session", null);
        var setCookie = sessionResponse.Headers.GetValues("Set-Cookie").First();
        var cookieValue = setCookie.Split(';')[0]; // "pf_monitoring_session=<token>"

        // Use cookie on verify endpoint
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/monitoring/verify");
        request.Headers.Add("Cookie", cookieValue);
        var response = await _anonClient!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Monitoring-User", out var userHeaders).Should().BeTrue();
        userHeaders!.First().Should().Be("test-admin");
    }

    [Fact]
    public async Task VerifySession_WithInvalidCookie_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/monitoring/verify");
        request.Headers.Add("Cookie", "pf_monitoring_session=invalid-token");
        var response = await _anonClient!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetStatus_AsAdmin_ReturnsMonitoringStatus()
    {
        var response = await _adminClient!.GetAsync("/api/monitoring/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("grafana");
        body.Should().Contain("jaeger");
        body.Should().Contain("prometheus");
    }

    [Fact]
    public async Task GetMetricsSummary_AsAdmin_ReturnsMetrics()
    {
        var response = await _adminClient!.GetAsync("/api/monitoring/metrics/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("requestsPerSecond");
        body.Should().Contain("errorRatePercent");
    }

    private record SessionResponse(bool Success, string? ExpiresAt);
}
