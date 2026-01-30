using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Infrastructure; // For AllProfilesResponseDto
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
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddConsole();

        // Add controllers support
        _ = builder.Services.AddControllers();

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

        // Profile services - use SQLite-cached service for fast queries
        _ = builder.Services.AddSingleton<CachedOrcaProfilesService>(sp =>
        {
            IUnifiedLoggingService logger = sp.GetRequiredService<IUnifiedLoggingService>();
            return new CachedOrcaProfilesService(logger);
        });
        _ = builder.Services.AddSingleton<ISlicerProfilesService>(sp => sp.GetRequiredService<CachedOrcaProfilesService>());
        _ = builder.Services.AddSingleton<IProfilePreloadService, ProfilePreloadService>(); // profile preload before readiness

        // Telemetry: provide a PrintFarmer telemetry implementation so UnifiedLoggingService can be constructed
        _ = builder.Services.AddSingleton<IPrintFarmerTelemetryService, PrintFarmerTelemetryService>();
        _ = builder.Services.AddScoped<IUnifiedLoggingService, UnifiedLoggingService>();

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

        // Enable routing and controller mapping
        _ = app.UseRouting();
        _ = app.MapControllers();

        _ = app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = c => c.Name == "liveness",
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
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
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    status = report.Status == HealthStatus.Healthy ? "ready" : "not-ready",
                    relaxed = relaxedReadiness,
                    timestamp = DateTime.UtcNow,
                    checks = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new { status = e.Value.Status.ToString(), description = e.Value.Description })
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

        _ = app.MapGet("/version", async (IOrcaBinaryDetector detector) =>
        {
            string? orcaVersion = await detector.GetVersionAsync();
            return Results.Ok(new
            {
                orcaslicerVersion = orcaVersion,
                workerVersion = "1.0.0",
                timestamp = DateTime.UtcNow
            });
        });

        IOrcaBinaryDetector orcaDetector = app.Services.GetRequiredService<IOrcaBinaryDetector>();
        if (!orcaDetector.IsRealBinaryPresent())
        {
            app.Logger.LogWarning("OrcaSlicer binary not present (stub in use) - readiness will be unhealthy for orca_binary.");
        }

        // Preload profiles before starting the app
        // This ensures profiles are cached in memory before the worker registers as ready
        try
        {
            IProfilePreloadService preloadService = app.Services.GetRequiredService<IProfilePreloadService>();
            await preloadService.PreloadProfilesAsync();
        }
        catch (Exception ex)
        {
            app.Logger.LogError("Failed to preload profiles at startup: {Exception}", ex.Message);
            throw; // Fail startup if profiles cannot be preloaded
        }

        await app.RunAsync();
    }
}
