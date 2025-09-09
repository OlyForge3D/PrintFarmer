using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Farm.OrcaSlicer.Worker.Health;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core; // shared worker core abstractions (IWorkerStateService, WorkerStateService, IProgressReporter, HttpProgressReporter, GracefulShutdownService, ISlicingPipelineService)
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
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // Redis
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var configuration = builder.Configuration;
            var raw = configuration.GetConnectionString("Redis") ?? "localhost:6379";
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
                Console.WriteLine($"[startup][redis] WARNING: Initial Redis connection failed: {ex.Message}");
                return ConnectionMultiplexer.Connect("localhost:6379,abortConnect=false");
            }
        });

        // HTTP clients
        builder.Services.AddHttpClient<HttpProgressReporter>(); // shared core implementation
        builder.Services.AddHttpClient<OrcaSlicingPipelineService>(); // engine-specific pipeline

        // Worker services (shared core + engine specific)
        builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>(); // shared
        builder.Services.AddSingleton<IOrcaBinaryDetector, OrcaBinaryDetector>(); // engine specific
        builder.Services.AddScoped<ISlicingPipelineService, OrcaSlicingPipelineService>(); // engine pipeline implements shared interface
        builder.Services.AddScoped<IProgressReporter, HttpProgressReporter>(); // shared

        // Background services (shared graceful shutdown + queue consumer derived)
        builder.Services.AddHostedService<GracefulShutdownService>(); // shared
        builder.Services.AddHostedService<QueueConsumerService>(); // derived

        // Health checks
        builder.Services.AddHealthChecks()
            .AddCheck<WorkerLivenessHealthCheck>("liveness")
            .AddCheck<WorkerReadinessHealthCheck>("readiness")
            .AddCheck<OrcaBinaryHealthCheck>("orca_binary")
            .AddCheck<RedisHealthCheck>("redis");

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.MapHealthChecks("/healthz", new HealthCheckOptions
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

        var relaxedEnv = Environment.GetEnvironmentVariable("WORKER_RELAXED_READINESS");
        var relaxedReadiness = !string.IsNullOrEmpty(relaxedEnv) && relaxedEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (relaxedReadiness)
        {
            app.Logger.LogWarning("WORKER_RELAXED_READINESS=true -> orca_binary will be excluded from readiness evaluation.");
        }

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
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

        app.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = c => (c.Name == "readiness" || c.Name == "redis" || c.Name == "orca_binary") && (!relaxedReadiness || c.Name != "orca_binary")
        });

        app.MapGet("/", (IOrcaBinaryDetector detector) => Results.Ok(new
        {
            service = "orcaslicer-worker",
            version = "1.0.0",
            status = "running",
            realBinary = detector.IsRealBinaryPresent(),
            capabilities = WorkerConstants.Capabilities
        }));

        var orcaDetector = app.Services.GetRequiredService<IOrcaBinaryDetector>();
        if (!orcaDetector.IsRealBinaryPresent())
        {
            app.Logger.LogWarning("OrcaSlicer binary not present (stub in use) - readiness will be unhealthy for orca_binary.");
        }

        app.Run();
    }
}
