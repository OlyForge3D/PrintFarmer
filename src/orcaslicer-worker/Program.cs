using Farm.OrcaSlicer.Worker.Health;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Web.Shared;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker;

internal static class WorkerConstants
{
    public static readonly string[] Capabilities = { "orcaslicer", "stl-processing", "gcode-generation" };
}

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Basic configuration
        builder.Services.AddLogging();
        builder.Services.AddHttpClient();

        // Worker state & graceful shutdown services
        builder.Services.AddSingleton<WorkerStateService>();
        builder.Services.AddHostedService<GracefulShutdownService>();
        builder.Services.AddSingleton<IOrcaBinaryDetector, OrcaBinaryDetector>();

        // Health checks
        var livenessTag = new[] { "liveness" };
        var readinessTag = new[] { "readiness" };
        builder.Services.AddHealthChecks()
            .AddCheck<WorkerLivenessHealthCheck>("worker_liveness", failureStatus: HealthStatus.Unhealthy, tags: livenessTag)
            .AddCheck<WorkerReadinessHealthCheck>("worker_readiness", failureStatus: HealthStatus.Degraded, tags: readinessTag)
            .AddCheck<OrcaBinaryHealthCheck>("orca_binary", failureStatus: HealthStatus.Unhealthy, tags: readinessTag);

        // Slicing pipeline
        builder.Services.AddTransient<IProgressReporter, HttpProgressReporter>();
        builder.Services.AddTransient<ISlicingPipelineService, OrcaSlicingPipelineService>();

        var app = builder.Build();

        // Log degraded mode if stub is detected
        var detector = app.Services.GetRequiredService<IOrcaBinaryDetector>();
        if (!detector.IsRealBinaryPresent())
        {
            app.Logger.LogWarning("OrcaSlicer binary not present (stub in use) - worker running in degraded mode. Slicing operations will fail until image rebuilt with real binary.");
        }

        // Root endpoint returns structured info (aligned with Prusa worker style)
        app.MapGet("/", () =>
        {
            return Results.Ok(new
            {
                service = "orcaslicer-worker",
                version = "1.0.0",
                status = "running",
                realBinary = detector.IsRealBinaryPresent(),
                capabilities = WorkerConstants.Capabilities
            });
        });

        // Environment flag to relax readiness (omit orca_binary from readiness predicate)
        var relaxed = Environment.GetEnvironmentVariable("WORKER_RELAXED_READINESS");
        var relaxedReadiness = !string.IsNullOrEmpty(relaxed) && relaxed.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (relaxedReadiness)
        {
            app.Logger.LogWarning("WORKER_RELAXED_READINESS=true -> orca_binary will be excluded from readiness evaluation.");
        }

        // Liveness should ONLY reflect core process vitality, exclude binary readiness.
        app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("liveness")
        });
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("liveness")
        });

        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("readiness") && (!relaxedReadiness || r.Name != "orca_binary"),
            ResponseWriter = async (ctx, report) =>
            {
                ctx.Response.ContentType = "application/json";
                var payload = new
                {
                    status = report.Status == HealthStatus.Healthy ? "ready" : "not-ready",
                    relaxed = relaxedReadiness,
                    timestamp = DateTime.UtcNow,
                    checks = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new { status = e.Value.Status.ToString(), description = e.Value.Description }
                    )
                };
                await ctx.Response.WriteAsJsonAsync(payload);
            }
        });

        app.Run();
    }
}
