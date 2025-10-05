using Farm.OrcaSlicer.Worker.Health;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core; // shared worker core abstractions (IWorkerStateService, WorkerStateService, IProgressReporter, HttpProgressReporter, GracefulShutdownService, ISlicingPipelineService)
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

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

        // Redis
        _ = builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            ConfigurationManager configuration = builder.Configuration;
            string raw = configuration.GetConnectionString("Redis") ?? "localhost:6379";
            if (!raw.Contains("abortConnect", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.TrimEnd(',') + ",abortConnect=false";
            }
            try
            {
                return ConnectionMultiplexer.Connect(raw);
            }
            catch (Exception ex)
            {
                // Resolve ILoggerFactory from the factory-provided service provider to avoid
                // creating a second service provider via BuildServiceProvider(). This
                // eliminates the ASP0000 diagnostic and ensures singletons are not duplicated.
                ILoggerFactory? loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
                ILogger? logger = loggerFactory?.CreateLogger("OrcaSlicer.Startup");
                if (logger != null)
                {
                    logger.LogWarning(ex, "[startup][redis] Initial Redis connection failed: {Message}", ex.Message);
                }
                else
                {
                    Console.WriteLine($"[startup][redis] WARNING: Initial Redis connection failed: {ex.Message}");
                }

                // Fall back to a local connect attempt with a safe default
                return ConnectionMultiplexer.Connect("localhost:6379,abortConnect=false");
            }
        });

        // HTTP clients
        _ = builder.Services.AddHttpClient<HttpProgressReporter>(); // shared core implementation
        _ = builder.Services.AddHttpClient<OrcaSlicingPipelineService>(); // engine-specific pipeline

        // Worker services (shared core + engine specific)
        _ = builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>(); // shared
        _ = builder.Services.AddSingleton<IOrcaBinaryDetector, OrcaBinaryDetector>(); // engine specific
        _ = builder.Services.AddScoped<ISlicingPipelineService, OrcaSlicingPipelineService>(); // engine pipeline implements shared interface
        _ = builder.Services.AddScoped<IProgressReporter, HttpProgressReporter>(); // shared


        _ = builder.Services.AddScoped<Farm.Infrastructure.Telemetry.IUnifiedLoggingService, Farm.Infrastructure.Telemetry.UnifiedLoggingService>();

        // Background services (shared graceful shutdown + queue consumer derived)
        _ = builder.Services.AddHostedService<GracefulShutdownService>(); // shared
        _ = builder.Services.AddHostedService<QueueConsumerService>(); // derived

        // Health checks
        _ = builder.Services.AddHealthChecks()
            .AddCheck<WorkerLivenessHealthCheck>("liveness")
            .AddCheck<WorkerReadinessHealthCheck>("readiness")
            .AddCheck<OrcaBinaryHealthCheck>("orca_binary")
            .AddCheck<RedisHealthCheck>("redis");

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
            Predicate = c => (c.Name == "readiness" || c.Name == "redis" || c.Name == "orca_binary") && (!relaxedReadiness || c.Name != "orca_binary"),
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
            Predicate = c => (c.Name == "readiness" || c.Name == "redis" || c.Name == "orca_binary") && (!relaxedReadiness || c.Name != "orca_binary")
        });

        _ = app.MapGet("/", (IOrcaBinaryDetector detector) => Results.Ok(new
        {
            service = "orcaslicer-worker",
            version = "1.0.0",
            status = "running",
            realBinary = detector.IsRealBinaryPresent(),
            capabilities = WorkerConstants.Capabilities
        }));

        IOrcaBinaryDetector orcaDetector = app.Services.GetRequiredService<IOrcaBinaryDetector>();
        if (!orcaDetector.IsRealBinaryPresent())
        {
            app.Logger.LogWarning("OrcaSlicer binary not present (stub in use) - readiness will be unhealthy for orca_binary.");
        }

        app.Run();
    }
}
