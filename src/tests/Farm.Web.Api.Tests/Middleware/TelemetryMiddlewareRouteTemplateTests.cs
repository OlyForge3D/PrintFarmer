using System.Diagnostics;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.Web.Api.Tests.Middleware;

/// <summary>
/// Regression tests for issue #2327 (TelemetryMiddleware tagged metrics/spans with the
/// raw request path, creating unbounded metric cardinality — one series per printer/job
/// GUID instead of one per route).
///
/// These drive real requests through a <see cref="TestServer"/> with
/// <see cref="TelemetryMiddleware"/> registered exactly as it is in <c>Program.cs</c>
/// (before <c>MapControllers</c>/<c>MapGet</c>), so they also lock in the ASP.NET Core
/// invariant the fix depends on: endpoint routing wraps the entire pipeline in the
/// minimal hosting model, so <c>HttpContext.GetEndpoint()</c> already reflects the
/// matched route by the time earlier-registered middleware runs.
/// </summary>
public class TelemetryMiddlewareRouteTemplateTests
{
    /// <summary>
    /// Captures the arguments passed to <see cref="IPrintFarmerTelemetryService.RecordApiCall"/>
    /// without needing a real meter/exporter.
    /// </summary>
    private sealed class RecordingTelemetryService : IPrintFarmerTelemetryService
    {
        public readonly List<(string Endpoint, string Method, int StatusCode)> RecordedCalls = new();

        public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) => null;

        public void RecordApiCall(string endpoint, string method, int statusCode, TimeSpan duration)
        {
            RecordedCalls.Add((endpoint, method, statusCode));
        }

        public void RecordPrinterOperation(string operation, string printerId, bool success)
        {
        }

        public void RecordSlicerOperation(string operation, string engine, bool success, TimeSpan? duration = null)
        {
        }

        public void RecordFileOperation(string operation, string fileType, long? fileSize = null)
        {
        }

        public void RecordDatabaseOperation(string table, string operation, int recordCount)
        {
        }

        public void RecordPagedQuery(string endpoint, int rowCount, long payloadBytes, bool cappedToMaxPageSize)
        {
        }
    }

    private static async Task<(IHost Host, RecordingTelemetryService Telemetry)> BuildHostAsync()
    {
        RecordingTelemetryService telemetry = new();

        // Uses WebApplicationBuilder/WebApplication rather than the classic
        // HostBuilder+Startup style, because Program.cs relies on the minimal hosting
        // model's implicit endpoint routing (no explicit UseRouting/UseEndpoints calls).
        // That implicit routing wraps the ENTIRE pipeline; the classic style only matches
        // routes at the point UseRouting is explicitly called, which would not exercise
        // the invariant this fix depends on.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton<IPrintFarmerTelemetryService>(telemetry);

        WebApplication app = builder.Build();

        // Mirrors Program.cs: telemetry middleware is registered before MapGet/MapControllers.
        _ = app.UseTelemetryMiddleware();

        _ = app.MapGet("/api/printers/{id}", (string id) => Results.Ok(new { id }));

        await app.StartAsync().ConfigureAwait(false);
        return (app, telemetry);
    }

    [Fact]
    public async Task ParameterizedRoute_TagsMatchedRouteTemplate_NotRawPath()
    {
        (IHost host, RecordingTelemetryService telemetry) = await BuildHostAsync();
        using IHost hostHandle = host;
        using HttpClient client = host.GetTestClient();

        using HttpResponseMessage response1 = await client.GetAsync("/api/printers/11111111-1111-1111-1111-111111111111");
        using HttpResponseMessage response2 = await client.GetAsync("/api/printers/22222222-2222-2222-2222-222222222222");

        Assert.Equal(2, telemetry.RecordedCalls.Count);

        // Both requests hit the same route template, so cardinality must stay bounded to
        // one series — never the raw per-instance path with the GUID embedded.
        Assert.All(telemetry.RecordedCalls, call => Assert.Equal("/api/printers/{id}", call.Endpoint));
        Assert.DoesNotContain(telemetry.RecordedCalls, call => call.Endpoint.Contains("11111111-1111-1111-1111-111111111111"));
        Assert.DoesNotContain(telemetry.RecordedCalls, call => call.Endpoint.Contains("22222222-2222-2222-2222-222222222222"));
    }

    [Fact]
    public async Task UnmatchedRoute_FallsBackToSingleUnknownBucket_NotRawPath()
    {
        (IHost host, RecordingTelemetryService telemetry) = await BuildHostAsync();
        using IHost hostHandle = host;
        using HttpClient client = host.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync("/api/does-not-exist/12345");

        _ = Assert.Single(telemetry.RecordedCalls);
        Assert.Equal("unknown", telemetry.RecordedCalls[0].Endpoint);
    }
}
