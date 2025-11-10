using Farm.Infrastructure.Telemetry;
using Farm.OrcaSlicer.Worker.Health;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core; // shared worker core abstractions (IWorkerStateService, WorkerStateService, IProgressReporter, HttpProgressReporter, GracefulShutdownService, ISlicingPipelineService)
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker;

internal static class WorkerConstants
{
    public static readonly string[] Capabilities = ["orcaslicer", "stl-processing", "gcode-generation"];
}

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddConsole();

        // HTTP clients (for API communication, artifact upload, and slicing pipeline)
        _ = builder.Services.AddHttpClient(); // Required for HttpJobPollerService
        _ = builder.Services.AddHttpClient<HttpProgressReporter>(); // shared core implementation
        _ = builder.Services.AddHttpClient<OrcaSlicingPipelineService>(); // engine-specific pipeline
        _ = builder.Services.AddHttpClient<SlicerRegistrationClient>(); // registration client

        // Worker services (shared core + engine specific)
        _ = builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>(); // shared
        _ = builder.Services.AddSingleton<IOrcaBinaryDetector, OrcaBinaryDetector>(); // engine specific
        _ = builder.Services.AddScoped<ISlicingPipelineService, OrcaSlicingPipelineService>(); // engine pipeline implements shared interface
        _ = builder.Services.AddScoped<IProgressReporter, HttpProgressReporter>(); // shared
        _ = builder.Services.AddSingleton<ISlicerRegistrationClient, SlicerRegistrationClient>(); // registration
        _ = builder.Services.AddSingleton<IOrcaProfilesService, OrcaProfilesService>(); // profiles discovery


        // Telemetry: provide a PrintFarmer telemetry implementation so UnifiedLoggingService can be constructed
        _ = builder.Services.AddSingleton<IPrintFarmerTelemetryService, PrintFarmerTelemetryService>();
        _ = builder.Services.AddScoped<Farm.Infrastructure.Telemetry.IUnifiedLoggingService, Farm.Infrastructure.Telemetry.UnifiedLoggingService>();

        // Background services (shared graceful shutdown + queue consumer derived)
        _ = builder.Services.AddHostedService<GracefulShutdownService>(); // shared
        _ = builder.Services.AddHostedService<QueueConsumerService>(); // derived
        _ = builder.Services.AddHostedService<RegistrationBackgroundService>(); // registration & heartbeat

        // Health checks
        _ = builder.Services.AddHealthChecks()
            .AddCheck<WorkerLivenessHealthCheck>("liveness")
            .AddCheck<WorkerReadinessHealthCheck>("readiness")
            .AddCheck<OrcaBinaryHealthCheck>("orca_binary");

        WebApplication app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            _ = app.UseDeveloperExceptionPage();
        }

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

        string? relaxedEnv = Environment.GetEnvironmentVariable("WORKER_RELAXED_READINESS");
        bool relaxedReadiness = !string.IsNullOrEmpty(relaxedEnv) && relaxedEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (relaxedReadiness)
        {
            app.Logger.LogWarning("WORKER_RELAXED_READINESS=true -> orca_binary will be excluded from readiness evaluation.");
        }

        _ = app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => (c.Name == "readiness" || c.Name == "orca_binary") && (!relaxedReadiness || c.Name != "orca_binary"),
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
            Predicate = c => (c.Name == "readiness" || c.Name == "orca_binary") && (!relaxedReadiness || c.Name != "orca_binary")
        });

        _ = app.MapGet("/", (IOrcaBinaryDetector detector) => Results.Ok(new
        {
            service = "orcaslicer-worker",
            version = "1.0.0",
            status = "running",
            realBinary = detector.IsRealBinaryPresent(),
            capabilities = WorkerConstants.Capabilities
        }));

        _ = app.MapGet("/profiles", async (IOrcaProfilesService profileService, CancellationToken ct) =>
        {
            var profiles = await profileService.ListAvailableProfilesAsync(ct);
            return Results.Ok(profiles);
        });
        // Endpoint: GET /profiles
        // Returns: List of available OrcaSlicer profiles from local installation

        IOrcaBinaryDetector orcaDetector = app.Services.GetRequiredService<IOrcaBinaryDetector>();
        if (!orcaDetector.IsRealBinaryPresent())
        {
            app.Logger.LogWarning("OrcaSlicer binary not present (stub in use) - readiness will be unhealthy for orca_binary.");
        }

        app.Run();
    }
}
