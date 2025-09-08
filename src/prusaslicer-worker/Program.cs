using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Farm.PrusaSlicer.Worker.Health;
using Farm.PrusaSlicer.Worker.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add Redis connection
builder.Services.AddSingleton<IConnectionMultiplexer>(provider =>
{
    var configuration = provider.GetService<IConfiguration>();
    var connectionString = configuration?.GetConnectionString("Redis") ?? "localhost:6379";
    return ConnectionMultiplexer.Connect(connectionString);
});

// Add HTTP client for API communication
builder.Services.AddHttpClient<HttpProgressReporter>();
builder.Services.AddHttpClient<PrusaSlicingPipelineService>();

// Add worker services
builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>();
builder.Services.AddScoped<ISlicingPipelineService, PrusaSlicingPipelineService>();
builder.Services.AddScoped<IProgressReporter, HttpProgressReporter>();

// Add background services
builder.Services.AddHostedService<GracefulShutdownService>();
builder.Services.AddHostedService<QueueConsumerService>();

// Add health checks
builder.Services.AddHealthChecks()
    .AddCheck<WorkerLivenessHealthCheck>("liveness")
    .AddCheck<WorkerReadinessHealthCheck>("readiness")
    .AddCheck("redis", () => 
    {
        try
        {
            var redis = builder.Services.BuildServiceProvider().GetRequiredService<IConnectionMultiplexer>();
            return redis.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Redis not connected");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis connection failed", ex);
        }
    });

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Liveness probe endpoint (simple ping)
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = check => check.Name == "liveness",
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new 
            { 
                status = report.Status == HealthStatus.Healthy ? "ok" : "unhealthy",
                timestamp = DateTime.UtcNow
            })
        );
    }
});

// Readiness probe endpoint (can accept work)
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Name == "readiness" || check.Name == "redis",
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new 
            { 
                status = report.Status == HealthStatus.Healthy ? "ready" : "not-ready",
                timestamp = DateTime.UtcNow,
                checks = report.Entries.ToDictionary(
                    entry => entry.Key,
                    entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description }
                )
            })
        );
    }
});

// Root endpoint
app.MapGet("/", () => Results.Ok(new 
{ 
    service = "prusaslicer-worker",
    version = "1.0.0",
    status = "running",
    capabilities = new[] { "prusaslicer", "stl-processing", "gcode-generation" }
}));

app.Run();