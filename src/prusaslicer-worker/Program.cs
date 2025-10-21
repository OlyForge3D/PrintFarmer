using Farm.Infrastructure.Telemetry;
using Farm.PrusaSlicer.Worker.Health;
using Farm.PrusaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core; // shared worker core abstractions (IWorkerStateService, WorkerStateService, IProgressReporter, HttpProgressReporter, GracefulShutdownService, ISlicingPipelineService)
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.PrusaSlicer.Worker;

internal static class WorkerConstants
{
    public static readonly string[] Capabilities = ["prusaslicer", "stl-processing", "gcode-generation"];
}

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Configure logging
        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddConsole();

        // HTTP clients (for API communication, artifact upload, and slicing pipeline)
        _ = builder.Services.AddHttpClient(); // Required for HttpJobPollerService
        _ = builder.Services.AddHttpClient<HttpProgressReporter>(); // shared progress reporter
        _ = builder.Services.AddHttpClient<PrusaSlicingPipelineService>(); // engine-specific pipeline

        // Worker services (shared + engine specific)
        _ = builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>(); // shared
        _ = builder.Services.AddSingleton<IPrusaBinaryDetector, PrusaBinaryDetector>(); // engine specific
        _ = builder.Services.AddScoped<ISlicingPipelineService, PrusaSlicingPipelineService>(); // engine pipeline implements shared interface
        _ = builder.Services.AddScoped<IProgressReporter, HttpProgressReporter>(); // shared
        // Telemetry: provide a PrintFarmer telemetry implementation so UnifiedLoggingService can be constructed
        _ = builder.Services.AddSingleton<IPrintFarmerTelemetryService, PrintFarmerTelemetryService>();
        _ = builder.Services.AddScoped<Farm.Infrastructure.Telemetry.IUnifiedLoggingService, Farm.Infrastructure.Telemetry.UnifiedLoggingService>();

        // Background services (shared graceful shutdown + queue consumer)
        _ = builder.Services.AddHostedService<GracefulShutdownService>(); // shared
        _ = builder.Services.AddHostedService<QueueConsumerService>(); // derived

        // Health checks
        _ = builder.Services.AddHealthChecks()
            .AddCheck<WorkerLivenessHealthCheck>("liveness")
            .AddCheck<WorkerReadinessHealthCheck>("readiness")
            .AddCheck<PrusaBinaryHealthCheck>("prusa_binary");

        WebApplication app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            _ = app.UseDeveloperExceptionPage();
        }

        // Liveness
        _ = app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = c => c.Name == "liveness",
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = report.Status == HealthStatus.Healthy ? "ok" : "unhealthy",
                    timestamp = DateTime.UtcNow
                }));
            }
        });

        // Relaxed readiness flag
        string? relaxedEnv = Environment.GetEnvironmentVariable("WORKER_RELAXED_READINESS");
        bool relaxedReadiness = !string.IsNullOrEmpty(relaxedEnv) && relaxedEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (relaxedReadiness)
        {
            app.Logger.LogWarning("WORKER_RELAXED_READINESS=true -> prusa_binary will be excluded from readiness evaluation.");
        }

        _ = app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => (c.Name == "readiness" || c.Name == "prusa_binary") && (!relaxedReadiness || c.Name != "prusa_binary"),
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = report.Status == HealthStatus.Healthy ? "ready" : "not-ready",
                    relaxed = relaxedReadiness,
                    timestamp = DateTime.UtcNow,
                    checks = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new { status = e.Value.Status.ToString(), description = e.Value.Description }
                    )
                }));
            }
        });

        _ = app.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = c => (c.Name == "readiness" || c.Name == "prusa_binary") && (!relaxedReadiness || c.Name != "prusa_binary")
        });

        _ = app.MapGet("/", (IPrusaBinaryDetector detector) => Results.Ok(new
        {
            service = "prusaslicer-worker",
            version = "1.0.0",
            status = "running",
            realBinary = detector.IsRealBinaryPresent(),
            capabilities = WorkerConstants.Capabilities
        }));

        IPrusaBinaryDetector prusaDetector = app.Services.GetRequiredService<IPrusaBinaryDetector>();
        if (!prusaDetector.IsRealBinaryPresent())
        {
            app.Logger.LogWarning("PrusaSlicer binary not present (stub in use) - readiness will be unhealthy for prusa_binary.");
        }

        app.Run();
    }
}
