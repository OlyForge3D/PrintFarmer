using Farm.Web.Server.Data;
using Farm.Web.Server.Services;
using Farm.Web.Server.Services.Interfaces;
using Farm.Web.Server.Hubs;
using Farm.Web.Server.Configuration;
using Farm.Web.Server.Health;
using Farm.Web.Server.Infrastructure;
using Farm.Web.Server.Middleware;
using Farm.Web.Server.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using FluentValidation;
using Farm.Web.Shared;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

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

// Database provider selection: SqlServer (default), Sqlite, Postgres, MySql
var dbProvider = builder.Configuration["Db:Provider"]
               ?? Environment.GetEnvironmentVariable("DB_PROVIDER")
               ?? "SqlServer";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    switch (dbProvider)
    {
        case "SqlServer":
        default:
            options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")
                                 ?? builder.Configuration.GetConnectionString("Default")
                                 ?? "Server=localhost,1433;Database=forgeiq;User Id=sa;Password=PrintFarm123!;TrustServerCertificate=True;",
                                 x => x.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"));
            break;
        case "Postgres":
        case "PostgreSql":
        case "PostgreSQL":
            options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
                               ?? builder.Configuration.GetConnectionString("Default")
                               ?? "Host=localhost;Database=forgeiq;Username=postgres;Password=postgres",
                               x => x.MigrationsHistoryTable("__EFMigrationsHistory", "public"));
            break;
        case "MySql":
        case "MySQL":
            {
                var cs = builder.Configuration.GetConnectionString("MySql")
                         ?? builder.Configuration.GetConnectionString("Default")
                         ?? "Server=localhost;Database=forgeiq;User=root;Password=example;";
                options.UseMySql(cs, ServerVersion.AutoDetect(cs),
                                 x => x.MigrationsHistoryTable("__EFMigrationsHistory"));
                break;
            }
        case "Sqlite":
            options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite")
                              ?? builder.Configuration.GetConnectionString("Default")
                              ?? "Data Source=farm.db",
                              x => x.MigrationsHistoryTable("__EFMigrationsHistory"));
            break;
    }
});

builder.Services.AddHttpClient<MoonrakerClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<IMoonrakerClient>(provider => provider.GetRequiredService<MoonrakerClient>());

builder.Services.AddHttpClient<PrusaLinkClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<IPrusaLinkClient>(provider => provider.GetRequiredService<PrusaLinkClient>());

builder.Services.AddHttpClient<SpoolmanService>();
builder.Services.AddScoped<ISpoolmanService>(provider => provider.GetRequiredService<SpoolmanService>());

builder.Services.AddScoped<SdcpClient>();
builder.Services.AddScoped<ISdcpClient>(provider => provider.GetRequiredService<SdcpClient>());

builder.Services.AddSignalR();
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<MoonrakerSubscriptionService>();
}

builder.Services.AddSingleton<PresetService>();
builder.Services.AddSingleton<IPresetService>(provider => provider.GetRequiredService<PresetService>());

builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<IDatabaseSeeder>(provider => provider.GetRequiredService<DatabaseSeeder>());

// === NEW INFRASTRUCTURE COMPONENTS ===

// Configuration validation
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection(AppSettings.SectionName));
builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection(DatabaseSettings.SectionName));
builder.Services.AddSingleton<ConfigurationValidator>();

// FluentValidation
builder.Services.AddScoped<IValidator<CreatePrinterDto>, CreatePrinterValidator>();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<ComprehensiveHealthCheck>("comprehensive")
    .AddDbContextCheck<AppDbContext>("database");

// Circuit breaker for resilience
builder.Services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();

// === END NEW INFRASTRUCTURE ===

// CORS: default to permissive in dev, restrict by env in prod
var allowedOrigins = builder.Configuration["AllowedOrigins"] ?? Environment.GetEnvironmentVariable("ALLOWED_ORIGINS");
builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy =>
    {
        if (!string.IsNullOrWhiteSpace(allowedOrigins))
        {
            var origins = allowedOrigins
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
                return;
            }
        }
        // Fallback for development
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Allow container/deployment scenarios to control DB init without EF migrations
    var initMode = Environment.GetEnvironmentVariable("DB_INIT_MODE")
                  ?? app.Configuration["Db:InitMode"]
                  ?? string.Empty;
    var disableEfMigrations = string.Equals(Environment.GetEnvironmentVariable("DISABLE_EF_MIGRATIONS"), "1", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(app.Configuration["DISABLE_EF_MIGRATIONS"], "true", StringComparison.OrdinalIgnoreCase);
    var provider = db.Database.ProviderName;
    if (provider != null && provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        db.Database.EnsureCreated();
    }
    else
    {
        // Provider-aware initialization with shared migrations
        var isSqlite = provider != null && provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var isPostgres = provider != null && provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var isSqlServer = provider != null && provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        var isMySql = provider != null && provider.Contains("MySql", StringComparison.OrdinalIgnoreCase);
        
        var forceEnsureCreated = disableEfMigrations || string.Equals(initMode, "EnsureCreated", StringComparison.OrdinalIgnoreCase);

        if (forceEnsureCreated)
        {
            Console.WriteLine($"[DB] Provider '{provider}' selected; running EnsureCreated (migrations disabled).");
            db.Database.EnsureCreated();
        }
        else
        {
            try 
            {
                // Try to run migrations - works for all providers if migrations exist
                Console.WriteLine($"[DB] Provider '{provider}' selected; applying migrations...");
                db.Database.Migrate();
                Console.WriteLine($"[DB] Migrations applied successfully for provider '{provider}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Migration failed for provider '{provider}': {ex.Message}");
                Console.WriteLine($"[DB] Falling back to EnsureCreated for schema initialization.");
                db.Database.EnsureCreated();
            }
        }

        // EF-based seeding for catalog data (idempotent)
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAllAsync();
    }
    
    // Validate configuration after services are built
    try
    {
        var configValidator = app.Services.GetRequiredService<ConfigurationValidator>();
        configValidator.ValidateConfiguration();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogCritical(ex, "Application startup failed due to configuration validation errors");
        throw;
    }
}

// === MIDDLEWARE PIPELINE ===
// Order matters - exception handling must be first

// Global exception handling (must be first)
app.UseMiddleware<GlobalExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Default");
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

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
app.MapGet("/api/presets", (PresetService svc) => Results.Ok(svc.GetPresets()));
app.MapPost("/api/presets", (PresetService svc, FilamentPresetsDto body) => { svc.SavePresets(body); return Results.NoContent(); });
// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");

app.Run();

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
