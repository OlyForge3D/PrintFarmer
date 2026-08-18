using System;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures OpenTelemetry tracing and metrics.
/// </summary>
public static class TelemetryStartup
{
    /// <summary>
    /// Adds PrintFarmer OpenTelemetry configuration (tracing + metrics).
    /// Skipped for tests and when <c>DISABLE_TELEMETRY=true</c> in configuration.
    /// </summary>
    public static IServiceCollection AddPrintFarmerTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // Configure OpenTelemetry (skippable for tests)
        bool disableTelemetry = string.Equals(
            configuration["DISABLE_TELEMETRY"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        // Also skip telemetry when running under the 'Testing' environment to avoid external exporters
        if (!disableTelemetry && !string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            // Determine console exporter setting once, outside lambdas, so both tracing and metrics can access it
            bool enableConsoleExporter = configuration.GetValue<bool>("OpenTelemetry:ConsoleExporter:Enabled", false);
            if (!enableConsoleExporter)
            {
                string? consoleEnv = Environment.GetEnvironmentVariable("OTEL_CONSOLE_EXPORTER");
                enableConsoleExporter = string.Equals(consoleEnv, "true", StringComparison.OrdinalIgnoreCase);
            }

            _ = services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                _ = resource.AddService("PrintFarmer.API", serviceVersion: "1.0.0")
                        .AddAttributes(new[]
                        {
                            new KeyValuePair<string, object>("farm.environment", environment.EnvironmentName),
                            new KeyValuePair<string, object>("farm.database.provider", configuration.GetValue<string>("DB_PROVIDER") ?? "sqlite")
                        });
            })
            .WithTracing(tracing =>
            {
                _ = tracing.AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = true;
                    options.EnrichWithHttpRequest = (activity, httpRequest) =>
                    {
                        _ = activity.SetTag("http.request.method", httpRequest.Method);
                        _ = activity.SetTag("http.request.path", httpRequest.Path);
                        if (httpRequest.QueryString.HasValue)
                        {
                            _ = activity.SetTag("http.request.query", httpRequest.QueryString.Value);
                        }
                    };
                    options.EnrichWithHttpResponse = (activity, httpResponse) =>
                    {
                        _ = activity.SetTag("http.response.status_code", httpResponse.StatusCode);
                    };
                })
                .AddHttpClientInstrumentation(options =>
                {
                    // #708 security: APNs URLs embed the raw device token in the path
                    // (`/3/device/<hex-token>`). The OTel HttpClient instrumentation
                    // derives `url.full` / `http.url` / `url.path` from the actual
                    // RequestUri, so a DelegatingHandler can't scrub it — the OWNER
                    // of that tag is the runtime's primary handler, below every
                    // DelegatingHandler. We must redact on the span itself.
                    options.EnrichWithHttpRequestMessage = EnrichApnsHttpRequest;
                    options.EnrichWithHttpResponseMessage = EnrichApnsHttpResponse;
                })
                .AddEntityFrameworkCoreInstrumentation(options =>
                {
                    // Note: SetDbStatementForStoredProcedure and SetDbStatementForText removed in .NET 10
                    options.EnrichWithIDbCommand = (activity, command) =>
                    {
                        _ = activity.SetTag("db.operation", command.CommandText);
                    };
                })
                .AddSource("PrintFarmer.*");

                // Add console exporter only if explicitly enabled (disabled by default to avoid log flooding)
                if (enableConsoleExporter)
                {
                    _ = tracing.AddConsoleExporter();
                }

                // Add OTLP exporter for production observability backends
                string? otlpEndpoint = configuration.GetValue<string>("OpenTelemetry:OTLP:Endpoint");
                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    _ = tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                        string? headers = configuration.GetValue<string>("OpenTelemetry:OTLP:Headers");
                        if (!string.IsNullOrEmpty(headers))
                        {
                            options.Headers = headers;
                        }
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                _ = metrics.AddAspNetCoreInstrumentation()
                       .AddHttpClientInstrumentation()
                       .AddRuntimeInstrumentation()
                       .AddMeter("PrintFarmer.Artifacts")
                       .AddMeter("PrintFarmer.Slicing")
                       .AddMeter("PrintFarmer.SlicerService")
                       .AddMeter("PrintFarmer.FailureDetection")
                       .AddMeter("PrintFarmer.API")
                       .AddMeter(Farm.Infrastructure.Services.Notifications.NativePush.NativePushMetrics.MeterName);

                // Add console exporter only if explicitly enabled (same as tracing)
                if (enableConsoleExporter)
                {
                    _ = metrics.AddConsoleExporter();
                }

                // Add Prometheus exporter for /metrics endpoint
                _ = metrics.AddPrometheusExporter();

                // Add OTLP exporter for metrics
                string? otlpEndpoint = configuration.GetValue<string>("OpenTelemetry:OTLP:Endpoint");
                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    _ = metrics.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                        string? headers = configuration.GetValue<string>("OpenTelemetry:OTLP:Headers");
                        if (!string.IsNullOrEmpty(headers))
                        {
                            options.Headers = headers;
                        }
                    });
                }
            });
        } // end skip-telemetry guard

        return services;
    }

    // -------------------------------------------------------------------------
    // #708 APNs URL redaction helpers.
    // Kept in this file so the redaction lives right next to the OTel wiring
    // that consumes it — if someone rewires HTTP instrumentation they will see
    // these callers immediately.
    // -------------------------------------------------------------------------

    /// <summary>Redacts APNs request URI tags on the production HttpClient activity.</summary>
    internal static void EnrichApnsHttpRequest(
        System.Diagnostics.Activity activity,
        HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(request);
        RedactApnsActivity(activity, request.RequestUri);
    }

    /// <summary>Re-applies APNs URI redaction when the production HttpClient activity completes.</summary>
    internal static void EnrichApnsHttpResponse(
        System.Diagnostics.Activity activity,
        HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(response);
        RedactApnsActivity(activity, response.RequestMessage?.RequestUri);
    }

    private static void RedactApnsActivity(
        System.Diagnostics.Activity activity,
        System.Uri? uri)
    {
        if (uri is null || !IsApnsHost(uri.Host))
        {
            return;
        }

        string redactedPath = RedactApnsTokenPath(uri.AbsolutePath);
        string redactedFull = uri.GetLeftPart(System.UriPartial.Authority) + redactedPath;
        _ = activity.SetTag("url.full", redactedFull);
        _ = activity.SetTag("http.url", redactedFull);
        _ = activity.SetTag("url.path", redactedPath);
        _ = activity.SetTag("http.request.path", redactedPath);

        // APNs does not use query strings. Remove both current and legacy OTel
        // query tags rather than retaining attacker-controlled/token-bearing data.
        _ = activity.SetTag("url.query", null);
        _ = activity.SetTag("http.request.query", null);
    }

    internal static readonly System.Text.RegularExpressions.Regex ApnsTokenPathRegex =
        new(@"(?<prefix>/3/device/).+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsApnsHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.Equals("api.push.apple.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("api.sandbox.push.apple.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("api.development.push.apple.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static string RedactApnsTokenPath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return absolutePath;
        }

        return ApnsTokenPathRegex.Replace(absolutePath, "${prefix}<REDACTED>");
    }
}
