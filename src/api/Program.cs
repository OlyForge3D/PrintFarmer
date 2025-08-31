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
        // Get allowed origins from environment variable or use defaults
        var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
                           ?? "http://localhost:8081,https://localhost:8443,http://localhost:5000,http://localhost:5001";

        // Check if wildcard network access is enabled
        var allowLocalNetwork = Environment.GetEnvironmentVariable("ALLOW_LOCAL_NETWORK") == "true";
        var networkRanges = Environment.GetEnvironmentVariable("ALLOWED_NETWORK_RANGES")
                           ?? "192.168.0.0/16,10.0.0.0/8,172.16.0.0/12";

        if (allowLocalNetwork)
        {
            // Allow any origin for local network development
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            policy.SetIsOriginAllowed(origin =>
            {
                // Always allow configured origins
                var configuredOrigins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                     .Select(o => o.Trim())
                                                     .ToArray();

                if (configuredOrigins.Contains(origin))
                    return true;

                // Check if origin matches allowed network ranges
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    return IsIpInAllowedRanges(uri.Host, networkRanges);
                }

                return false;
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
        }
    });
});

// Helper method to check if IP is in allowed network ranges
static bool IsIpInAllowedRanges(string host, string networkRanges)
{
    try
    {
        if (!System.Net.IPAddress.TryParse(host, out var ipAddress))
            return false;

        var ranges = networkRanges.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var range in ranges)
        {
            var parts = range.Trim().Split('/');
            if (parts.Length != 2) continue;

            if (System.Net.IPAddress.TryParse(parts[0], out var networkAddress) &&
                int.TryParse(parts[1], out var prefixLength))
            {
                if (IsIpInNetwork(ipAddress, networkAddress, prefixLength))
                    return true;
            }
        }

        return false;
    }
    catch
    {
        return false;
    }
}

// Helper method to check if IP is in network range
static bool IsIpInNetwork(System.Net.IPAddress ipAddress, System.Net.IPAddress networkAddress, int prefixLength)
{
    var ipBytes = ipAddress.GetAddressBytes();
    var networkBytes = networkAddress.GetAddressBytes();

    if (ipBytes.Length != networkBytes.Length)
        return false;

    var bytesToCheck = prefixLength / 8;
    var bitsToCheck = prefixLength % 8;

    for (int i = 0; i < bytesToCheck; i++)
    {
        if (ipBytes[i] != networkBytes[i])
            return false;
    }

    if (bitsToCheck > 0 && bytesToCheck < ipBytes.Length)
    {
        var mask = (byte)(0xFF << (8 - bitsToCheck));
        if ((ipBytes[bytesToCheck] & mask) != (networkBytes[bytesToCheck] & mask))
            return false;
    }

    return true;
}

// Database provider selection: Sqlite (default), SqlServer, Postgres, MySql
var dbProvider = builder.Configuration["Db:Provider"]
               ?? Environment.GetEnvironmentVariable("DB_PROVIDER")
               ?? "Sqlite";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    switch (dbProvider)
    {
        case "SqlServer":
            options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")
                                 ?? builder.Configuration.GetConnectionString("Default")
                                 ?? "Server=localhost,1433;Database=printfarmer;User Id=sa;Password=PrintFarm123!;TrustServerCertificate=True;",
                                 o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"));
            break;
        case "Postgres":
            options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
                               ?? builder.Configuration.GetConnectionString("Default")
                               ?? "Host=localhost;Database=printfarmer;Username=printfarmer;Password=PrintFarm123!",
                               o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public"));
            break;
        case "MySql":
            {
                var cs = builder.Configuration.GetConnectionString("MySql")
                         ?? builder.Configuration.GetConnectionString("Default")
                         ?? "Server=localhost;Database=printfarmer;User=printfarmer;Password=PrintFarm123!;";
                var serverVersion = ServerVersion.AutoDetect(cs);
                options.UseMySql(cs, serverVersion);
                break;
            }
        case "Sqlite":
        default:
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
builder.Services.AddScoped<DatabaseInitializer>();
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

// Database initialization with retry logic for resilient startup
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var dbInitializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();

    // Get retry configuration from environment variables (lower defaults for development)
    var retryCount = int.TryParse(app.Configuration["DB_CONNECTION_RETRY_COUNT"], out var rc) ? rc : 3;
    var retryDelay = int.TryParse(app.Configuration["DB_CONNECTION_RETRY_DELAY"], out var rd) ? rd : 2;

    try
    {
        await dbInitializer.InitializeAsync(dbProvider, retryCount, retryDelay);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "[DB] Failed to initialize database after all retry attempts. Application cannot start.");
        if (dbProvider != "Sqlite")
        {
            logger.LogInformation("[DB] If using external database (SQL Server, PostgreSQL, MySQL), ensure:");
            logger.LogInformation("[DB] 1. Database server is running and accessible");
            logger.LogInformation("[DB] 2. Connection string is correct");
            logger.LogInformation("[DB] 3. Database server is ready to accept connections");
            logger.LogInformation("[DB] 4. Network connectivity allows database access");
        }
        throw;
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
