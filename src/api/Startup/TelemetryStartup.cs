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
    /// Skipped for tests and when DISABLE_TELEMETRY=true.
    /// </summary>
    public static IServiceCollection AddPrintFarmerTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // Configure OpenTelemetry (skippable for tests)
        bool disableTelemetry = false;
        try
        {
            string? disableEnv = Environment.GetEnvironmentVariable("DISABLE_TELEMETRY");
            if (!string.IsNullOrEmpty(disableEnv) && string.Equals(disableEnv, "true", StringComparison.OrdinalIgnoreCase))
            {
                disableTelemetry = true;
            }
        }
        catch
        { /* best-effort */
        }

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
                    options.EnrichWithHttpRequestMessage = (activity, request) =>
                    {
                        System.Uri? uri = request.RequestUri;
                        if (uri is null)
                        {
                            return;
                        }

                        if (!IsApnsHost(uri.Host))
                        {
                            return;
                        }

                        string redactedPath = RedactApnsTokenPath(uri.AbsolutePath);

                        // Drop uri.Query entirely for APNs — RFC 7540 APNs
                        // never legitimately uses query strings, so any
                        // content in Query is either accidental or a token
                        // that leaked through URI parsing. Zero-trust:
                        // scrub it (Hicks v4 blocker 1).
                        string redactedFull = uri.GetLeftPart(System.UriPartial.Authority) + redactedPath;

                        _ = activity.SetTag("url.full", redactedFull);
                        _ = activity.SetTag("http.url", redactedFull);
                        _ = activity.SetTag("url.path", redactedPath);
                        _ = activity.SetTag("http.request.path", redactedPath);
                    };
                    options.EnrichWithHttpResponseMessage = (activity, response) =>
                    {
                        System.Uri? uri = response.RequestMessage?.RequestUri;
                        if (uri is null || !IsApnsHost(uri.Host))
                        {
                            return;
                        }

                        string redactedPath = RedactApnsTokenPath(uri.AbsolutePath);

                        // Same rationale as the request enricher: drop Query
                        // to defend against tokens surfacing there.
                        string redactedFull = uri.GetLeftPart(System.UriPartial.Authority) + redactedPath;

                        // Re-apply on completion — some processors read tags on span end.
                        _ = activity.SetTag("url.full", redactedFull);
                        _ = activity.SetTag("http.url", redactedFull);
                        _ = activity.SetTag("url.path", redactedPath);
                        _ = activity.SetTag("http.request.path", redactedPath);
                    };
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
