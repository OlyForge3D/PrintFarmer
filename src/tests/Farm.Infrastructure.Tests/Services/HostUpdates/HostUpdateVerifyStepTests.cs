using System.Net;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Kane audit P0.5 / Bishop+Hicks review: the executor's verify step must require the exact
/// aggregated <c>/health</c> report status ("Healthy"), never merely an HTTP 200 -- ASP.NET
/// Core's default health check middleware also returns 200 for "Degraded", and the previously
/// wired <c>/healthz</c> liveness probe is a hardcoded stub that always returns 200
/// unconditionally. The real endpoint is serialized with
/// <c>Program.HealthJsonOptions</c> (<c>PropertyNamingPolicy = JsonNamingPolicy.CamelCase</c>),
/// so the wire property is <c>status</c>, not <c>Status</c> -- earlier tests here used a
/// hand-built PascalCase fixture that happened to match the (buggy) PascalCase-only lookup,
/// hiding the mismatch. <see cref="IsHealthyAsync_RealCamelCaseSerializedFixture_ReturnsTrue"/>
/// and <see cref="IsHealthyAsync_RealCamelCaseSerializedDegradedFixture_ReturnsFalse"/> below use
/// the actual lowercase-property shape the endpoint really emits.
/// </summary>
public sealed class AggregateHostUpdateHealthCheckTests
{
    private static AggregateHostUpdateHealthCheck CreateCheck(HttpStatusCode statusCode, string body)
    {
        var handler = new StubHttpMessageHandler(statusCode, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new AggregateHostUpdateHealthCheck("api-comprehensive-health", client, "/health");
    }

    [Fact]
    public async Task IsHealthyAsync_ExactlyHealthyStatus_ReturnsTrue()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, """{"Status":"Healthy","Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHealthyAsync_RealCamelCaseSerializedFixture_ReturnsTrue()
    {
        // This is the actual shape ProgramHelpers.WriteHealthResponseAsync produces via
        // Program.HealthJsonOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase): the
        // top-level "status" property key is lowercase even though the enum value itself
        // ("Healthy") is not renamed by a naming policy.
        AggregateHostUpdateHealthCheck check = CreateCheck(
            HttpStatusCode.OK,
            """{"status":"Healthy","totalChecksDuration":"00:00:00.0120000","results":{"comprehensive":{"status":"Healthy","duration":"00:00:00.0050000"}}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHealthyAsync_RealCamelCaseSerializedDegradedFixture_ReturnsFalse()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(
            HttpStatusCode.OK,
            """{"status":"Degraded","totalChecksDuration":"00:00:00.0120000","results":{"spoolman":{"status":"Degraded","duration":"00:00:00.0050000"}}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_DegradedStatusWithHttp200_ReturnsFalse()
    {
        // ASP.NET Core's default health check middleware maps both Healthy and Degraded to HTTP 200;
        // a naive "response.IsSuccessStatusCode" check would incorrectly treat this as healthy.
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, """{"Status":"Degraded","Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_UnhealthyStatusWithHttp503_ReturnsFalse()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.ServiceUnavailable, """{"Status":"Unhealthy","Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_MalformedJsonBody_ReturnsFalseRatherThanThrowing()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, "not json");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_MissingStatusProperty_ReturnsFalse()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, """{"Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            };
            return Task.FromResult(response);
        }
    }
}
