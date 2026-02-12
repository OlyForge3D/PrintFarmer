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
                .AddHttpClientInstrumentation()
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
                       .AddMeter("PrintFarmer.API");

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
}
