using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Farm.Slicer.Worker.Health;

var builder = WebApplication.CreateBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add health checks
builder.Services.AddHealthChecks()
    .AddCheck<WorkerLivenessHealthCheck>("liveness")
    .AddCheck<WorkerReadinessHealthCheck>("readiness");

// Add worker services
builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>();
builder.Services.AddHostedService<GracefulShutdownService>();

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
    Predicate = check => check.Name == "readiness",
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new 
            { 
                status = report.Status == HealthStatus.Healthy ? "ready" : "not-ready",
                timestamp = DateTime.UtcNow
            })
        );
    }
});

// Root endpoint
app.MapGet("/", () => Results.Ok(new 
{ 
    service = "slicer-worker",
    version = "1.0.0",
    status = "running"
}));

app.Run();