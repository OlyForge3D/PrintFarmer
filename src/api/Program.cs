using Farm.Web.Api.Data;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Configuration;
using Farm.Web.Api.Health;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;
using Farm.Web.Shared;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add API services
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Configure JSON options for .NET 9 compatibility
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = false;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure JSON options for minimal APIs
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// CORS configuration for API access
builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy =>
    {
        policy.WithOrigins("http://localhost:8081", "https://localhost:8443", "http://localhost:5000", "http://localhost:5001")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Database provider selection: Sqlite (default), SqlServer, Postgres, MySql
var dbProvider = builder.Configuration["Db:Provider"]
               ?? Environment.GetEnvironmentVariable("DB_PROVIDER")
               ?? "Sqlite";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    switch (dbProvider)
    {
        case "SqlServer":
        default:
            options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")
                                 ?? builder.Configuration.GetConnectionString("Default")
                                 ?? "Server=localhost,1433;Database=forgeiq;User Id=sa;Password=PrintFarm123!;TrustServerCertificate=True;",
                                 o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"));
            break;
        case "Postgres":
            options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
                               ?? builder.Configuration.GetConnectionString("Default")
                               ?? "Host=localhost;Database=forgeiq;Username=forgeiq;Password=PrintFarm123!",
                               o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public"));
            break;
        case "MySql":
            {
                var cs = builder.Configuration.GetConnectionString("MySql")
                         ?? builder.Configuration.GetConnectionString("Default")
                         ?? "Server=localhost;Database=forgeiq;User=forgeiq;Password=PrintFarm123!;";
                var serverVersion = ServerVersion.AutoDetect(cs);
                options.UseMySql(cs, serverVersion);
                break;
            }
        case "Sqlite":
            options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite")
                              ?? builder.Configuration.GetConnectionString("Default")
                              ?? "Data Source=farm.db");
            break;
    }
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// HTTP clients for external APIs
builder.Services.AddHttpClient<MoonrakerClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<PrusaLinkClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<SpoolmanService>("SpoolmanService", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Register services with interfaces
builder.Services.AddScoped<IPresetService, PresetService>();
builder.Services.AddScoped<ISpoolmanService, SpoolmanService>();
builder.Services.AddScoped<INetworkDiscoveryService, NetworkDiscoveryService>();
builder.Services.AddScoped<INetworkDiscoverySettingsService, NetworkDiscoverySettingsService>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<ConfigurationValidator>();
builder.Services.AddScoped<IMoonrakerClient, MoonrakerClient>();
builder.Services.AddScoped<IPrusaLinkClient, PrusaLinkClient>();
builder.Services.AddScoped<ISdcpClient, SdcpClient>();
builder.Services.AddScoped<ICircuitBreakerService, CircuitBreakerService>();

// Background services
builder.Services.AddHostedService<MoonrakerSubscriptionService>();

// SignalR for real-time updates
builder.Services.AddSignalR();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<ComprehensiveHealthCheck>("comprehensive");

// Validation
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

var app = builder.Build();

// Database initialization and seeding
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
    logger.LogInformation("[DB] Starting database initialization for provider: {DbProvider}", dbProvider);
    
    try
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("[DB] Database migration completed successfully.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[DB] Migration failed for provider '{Provider}': {Message}. Falling back to EnsureCreated.", dbProvider, ex.Message);
        db.Database.EnsureCreated();
    }
    
    // EF-based seeding for catalog data (idempotent)
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAllAsync();
    
    // Validate configuration after services are built
    try
    {
        var configValidator = scope.ServiceProvider.GetRequiredService<ConfigurationValidator>();
        configValidator.ValidateConfiguration();
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Application startup failed due to configuration validation errors");
        throw;
    }
}

// === MIDDLEWARE PIPELINE ===

// Global exception handling
app.UseMiddleware<GlobalExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Default");

// Configure API routing and SignalR hubs
app.MapControllers();
app.MapHub<PrinterHub>("/hubs/printers");

// Health checks
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = JsonSerializer.Serialize(new
        {
            Status = report.Status.ToString(),
            TotalChecksDuration = report.TotalDuration,
            Results = report.Entries.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    Status = kvp.Value.Status.ToString(),
                    Duration = kvp.Value.Duration,
                    Description = kvp.Value.Description,
                    Data = kvp.Value.Data
                })
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        
        await context.Response.WriteAsync(result);
    }
});

// Minimal API for presets
app.MapGet("/api/presets", ([FromServices] IPresetService svc) => Results.Ok(svc.GetPresets()));
app.MapPost("/api/presets", ([FromServices] IPresetService svc, [FromBody] FilamentPresetsDto body) => { svc.SavePresets(body); return Results.NoContent(); });

// Minimal API for network discovery settings
app.MapGet("/api/network-discovery/settings", ([FromServices] INetworkDiscoverySettingsService svc) => Results.Ok(svc.GetSettings()));
app.MapPost("/api/network-discovery/settings", ([FromServices] INetworkDiscoverySettingsService svc, [FromBody] NetworkDiscoverySettingsDto body) => { svc.SaveSettings(body); return Results.NoContent(); });
app.MapGet("/api/network-discovery/dynamic-ranges", ([FromServices] INetworkDiscoverySettingsService svc) => Results.Ok(svc.GetDynamicNetworkRanges()));

// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
