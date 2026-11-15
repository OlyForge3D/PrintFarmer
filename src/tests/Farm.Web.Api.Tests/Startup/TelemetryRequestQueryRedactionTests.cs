using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Farm.Web.Api.Tests.Startup;

[CollectionDefinition("Request query telemetry export", DisableParallelization = true)]
public sealed class RequestQueryTelemetryExportCollection;

[Collection("Request query telemetry export")]
public sealed class TelemetryRequestQueryRedactionTests
{
    private const string RedactionOptOut = "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION";
    private const string HubMarker = "synthetic-hub-query-marker";
    private const string ViewerMarker = "synthetic-viewer-query-marker";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProductionTelemetry_QueryRequests_ExportsNoQueryValues(bool disableRedaction, bool consoleEnabled)
    {
        string? previousRedaction = Environment.GetEnvironmentVariable(RedactionOptOut);
        string? previousConsole = Environment.GetEnvironmentVariable("OTEL_CONSOLE_EXPORTER");
        TextWriter previousOutput = Console.Out;
        using StringWriter output = new(CultureInfo.InvariantCulture);
        using RecordingOtlpHandler otlp = new();
        try
        {
            Environment.SetEnvironmentVariable(RedactionOptOut, disableRedaction.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("OTEL_CONSOLE_EXPORTER", "false");
            Console.SetOut(output);
            WebApplicationBuilder builder = CreateBuilder("TelemetryTests");
            builder.Configuration["OpenTelemetry:ConsoleExporter:Enabled"] = consoleEnabled.ToString(CultureInfo.InvariantCulture);
            if (consoleEnabled)
            {
                builder.Configuration["OpenTelemetry:OTLP:Endpoint"] = "http://localhost:4318/v1/traces";
            }
            builder.Services.PostConfigureAll<OtlpExporterOptions>(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.HttpClientFactory = () => new HttpClient(otlp, disposeHandler: false);
            });
            builder.Services.AddPrintFarmerTelemetry(builder.Configuration, builder.Environment);
            ConcurrentQueue<SpanSnapshot> immediateSpans = new();
            ConcurrentQueue<SpanSnapshot> batchedSpans = new();
            SnapshotExporter immediateExporter = new(immediateSpans, 2);
            SnapshotExporter batchedExporter = new(batchedSpans, 2);
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddProcessor(new SimpleActivityExportProcessor(immediateExporter))
                .AddProcessor(new BatchActivityExportProcessor(batchedExporter)));
            await using WebApplication app = builder.Build();
            app.MapGet("/hubs/printers/negotiate", (HttpContext context) =>
                Results.Text(context.Request.Query["access_token"].ToString(), statusCode: 202));
            app.MapGet("/api/viewer/download", (HttpContext context) =>
                Results.Text(context.Request.Query["path"].ToString()));
            await app.StartAsync();
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage hubResponse = await client.GetAsync("/hubs/printers/negotiate?access_token=" + HubMarker);
            Assert.Equal(System.Net.HttpStatusCode.Accepted, hubResponse.StatusCode);
            Assert.Equal(HubMarker, await hubResponse.Content.ReadAsStringAsync());
            using HttpResponseMessage viewerResponse = await client.GetAsync("/api/viewer/download?path=" + ViewerMarker + "&other=" + HubMarker);
            Assert.Equal(System.Net.HttpStatusCode.OK, viewerResponse.StatusCode);
            Assert.Equal(ViewerMarker, await viewerResponse.Content.ReadAsStringAsync());
            await immediateExporter.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(app.Services.GetRequiredService<TracerProvider>().ForceFlush());
            await batchedExporter.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            AssertExportedRequests(immediateSpans);
            AssertExportedRequests(batchedSpans);
            string consoleOutput = output.ToString();
            Assert.DoesNotContain(HubMarker, consoleOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(ViewerMarker, consoleOutput, StringComparison.Ordinal);
            if (consoleEnabled)
            {
                Assert.Contains("http.route", consoleOutput, StringComparison.Ordinal);
                Assert.Contains("/api/viewer/download", consoleOutput, StringComparison.Ordinal);
                string[] payloads = otlp.TracePayloads.ToArray();
                Assert.NotEmpty(payloads);
                string payload = string.Concat(payloads);
                Assert.Contains("http.route", payload, StringComparison.Ordinal);
                Assert.Contains("/hubs/printers/negotiate", payload, StringComparison.Ordinal);
                Assert.Contains("/api/viewer/download", payload, StringComparison.Ordinal);
                Assert.DoesNotContain(HubMarker, payload, StringComparison.Ordinal);
                Assert.DoesNotContain(ViewerMarker, payload, StringComparison.Ordinal);
                Assert.DoesNotContain("http.request.query", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("url.query", payload, StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(consoleOutput);
                Assert.Empty(otlp.TracePayloads);
            }
        }
        finally
        {
            Console.SetOut(previousOutput);
            Environment.SetEnvironmentVariable(RedactionOptOut, previousRedaction);
            Environment.SetEnvironmentVariable("OTEL_CONSOLE_EXPORTER", previousConsole);
        }
    }

    [Fact]
    public async Task ProductionTelemetry_LateUrlAttributes_RemovesQueryBeforeExport()
    {
        WebApplicationBuilder builder = CreateBuilder("TelemetryTests");
        builder.Services.AddPrintFarmerTelemetry(builder.Configuration, builder.Environment);
        ConcurrentQueue<SpanSnapshot> spans = new();
        SnapshotExporter exporter = new(spans, 1);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(exporter)));
        await using WebApplication app = builder.Build();
        app.MapGet("/api/viewer/download", () =>
        {
            Activity activity = Activity.Current!;
            activity.SetTag("url.full", "http://localhost/api/viewer/download?path=" + ViewerMarker);
            activity.SetTag("http.url", "http://localhost/api/viewer/download?path=" + ViewerMarker);
            activity.SetTag("http.target", "/api/viewer/download?path=" + ViewerMarker);
            activity.SetTag("url.query", "path=" + ViewerMarker);
            activity.SetTag("http.request.query", "path=" + ViewerMarker);
            return Results.Ok();
        });
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync("/api/viewer/download?path=" + ViewerMarker);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        await exporter.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(app.Services.GetRequiredService<TracerProvider>().ForceFlush());

        SpanSnapshot span = Assert.Single(spans);
        AssertNoQueryValues(span);
        Assert.Equal("http://localhost/api/viewer/download", span.Tags["url.full"]);
        Assert.Equal("http://localhost/api/viewer/download", span.Tags["http.url"]);
        Assert.Equal("/api/viewer/download", span.Tags["http.target"]);
    }

    [Theory]
    [InlineData("Testing", false)]
    [InlineData("TelemetryTests", true)]
    public void ProductionTelemetry_Disabled_DoesNotRegisterTracing(string environment, bool disabled)
    {
        WebApplicationBuilder builder = CreateBuilder(environment);
        builder.Configuration["DISABLE_TELEMETRY"] = disabled.ToString(CultureInfo.InvariantCulture);
        builder.Services.AddPrintFarmerTelemetry(builder.Configuration, builder.Environment);

        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(TracerProvider));
    }

    private static WebApplicationBuilder CreateBuilder(string environment)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        return builder;
    }

    private static void AssertExportedRequests(IEnumerable<SpanSnapshot> spans)
    {
        SpanSnapshot[] requests = spans.ToArray();
        Assert.Equal(2, requests.Length);
        SpanSnapshot hub = Assert.Single(requests, span => Equals(span.Tags["http.route"], "/hubs/printers/negotiate"));
        SpanSnapshot viewer = Assert.Single(requests, span => Equals(span.Tags["http.route"], "/api/viewer/download"));
        Assert.Equal(202, hub.Tags["http.response.status_code"]);
        Assert.Equal(200, viewer.Tags["http.response.status_code"]);
        Assert.All(requests, span =>
        {
            Assert.Equal(ActivityKind.Server, span.Kind);
            Assert.True(span.Recorded);
            Assert.Equal("GET", span.Tags["http.request.method"]);
            AssertNoQueryValues(span);
        });
    }

    private static void AssertNoQueryValues(SpanSnapshot span)
    {
        foreach (string value in span.Values.Append(span.DisplayName))
        {
            Assert.DoesNotContain(HubMarker, value, StringComparison.Ordinal);
            Assert.DoesNotContain(ViewerMarker, value, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("url.query", span.Tags.Keys);
        Assert.DoesNotContain("http.request.query", span.Tags.Keys);
    }

    private sealed record SpanSnapshot(
        string DisplayName, ActivityKind Kind, bool Recorded,
        Dictionary<string, object?> Tags, string[] Values);

    private sealed class RecordingOtlpHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> TracePayloads { get; } = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using MemoryStream body = new();
            request.Content!.ReadAsStream(cancellationToken).CopyTo(body);
            return Capture(request, body.ToArray());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Capture(request, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        }

        private HttpResponseMessage Capture(HttpRequestMessage request, byte[] body)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/traces")
            {
                Assert.Equal("application/x-protobuf", request.Content!.Headers.ContentType!.MediaType);
                TracePayloads.Enqueue(Encoding.UTF8.GetString(body));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }

    private sealed class SnapshotExporter(ConcurrentQueue<SpanSnapshot> spans, int expectedCount) : BaseExporter<Activity>
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (Activity activity in batch)
            {
                spans.Enqueue(new(
                    activity.DisplayName, activity.Kind, activity.Recorded,
                    activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value),
                    activity.TagObjects.Concat(activity.Events.SelectMany(activityEvent => activityEvent.Tags))
                        .Select(tag => tag.Value?.ToString() ?? string.Empty).ToArray()));
            }
            if (spans.Count >= expectedCount)
            {
                Completion.TrySetResult();
            }

            return ExportResult.Success;
        }
    }
}
