using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Api;
using Farm.Web.Api;
using Farm.Web.Api.Health;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Infrastructure.Database;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Artifacts;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Api.Startup;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;

// using Microsoft.Extensions.Caching.Memory; // removed unused
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register DLL import resolver for Lib3MF to handle cross-platform native library loading
// Maps "lib3mf.dll" to platform-specific names: lib3mf.so (Linux), lib3mf.dylib (macOS), lib3mf.dll (Windows)
// The assembly resolver can only be set once per AppDomain, so we attempt and catch if already set
try
{
    NativeLibrary.SetDllImportResolver(typeof(Lib3MF.Internal.Lib3MFWrapper).Assembly, (name, assembly, searchPath) =>
    {
        if (name != "lib3mf.dll")
        {
            return IntPtr.Zero;
        }

        string libName = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "lib3mf.so" :
                         RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "lib3mf.dylib" : "lib3mf.dll";

        return NativeLibrary.TryLoad(libName, assembly, searchPath, out var handle) ? handle : IntPtr.Zero;
    });
}
catch (InvalidOperationException)
{
    // Resolver already set from a previous Program.cs invocation in the same AppDomain
    // This is expected behavior in integration tests where multiple app instances are created
    // The previously-set resolver will handle all library loading for this assembly
}

// Explicitly add environment variables with "PFARM__" prefix to configuration.
// This allows settings like PFARM__Spoolman__BaseUrl to be recognized by the
// configuration binding system. The "__" separator becomes ":" in the configuration hierarchy.
// Example: PFARM__Spoolman__BaseUrl → Spoolman:BaseUrl configuration section
builder.Configuration.AddEnvironmentVariables("PFARM__");

// When running tests that use the shared in-memory SQLite fixture we may need
// to register test-only services (pre-seed, fallback DbContextFactory, etc.)
// before host build-time DI validation runs. Disable ValidateOnBuild in that
// specific test scenario so the test factory can configure the required
// services and pre-seed the database. This only applies when the
// TEST_USE_SHARED_SQLITE environment variable is set and does not change
// normal production behavior.
try
{
    if (string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SHARED_SQLITE"), "true", StringComparison.OrdinalIgnoreCase))
    {
        _ = builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });
    }
}
catch
{
    // Best-effort; do not fail startup if environment or hosting APIs unavailable
}

// Register database with multi-provider support
builder.Services.AddPrintFarmerDatabase(builder.Configuration);

// Configure Data Protection for encrypting sensitive data (API keys, passwords)
builder.Services.AddPrintFarmerDataProtection(builder.Environment, builder.Environment.ContentRootPath);

// Register all PrintFarmer services
builder.Services.AddPrintFarmerServices(builder.Configuration, builder.Environment);

// Register slicer module (SlicerDbContext, module repositories, metrics, and configuration).
// During transition both AppDbContext and SlicerDbContext coexist sharing the same underlying database.
builder.Services.AddSlicerModule(builder.Configuration);

// Register stub implementations for slicer module service interfaces.
// These are no-op proxies used until real service implementations are migrated into the module.
builder.Services.AddSlicerModuleStubServices();

// Register SystemLog logger provider to capture all application logs to the database
builder.Logging.AddSystemLogProvider(LogLevel.Information);

// Register settings service
// Bind system-level settings from IConfiguration so they are available before any DB access during startup.
// This ensures POCOs like DatabaseSettings are configured from env/config without needing AppDbContext.
builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection(Farm.Infrastructure.Settings.DatabaseSettings.SectionName));
builder.Services.AddPrintFarmerSettings();

// Attempt to unify WebRoot to repository-level /wwwroot directory (shared across API & React build output)
try
{
    string potentialShared = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "wwwroot"));
    if (Directory.Exists(potentialShared))
    {
        builder.Environment.WebRootPath = potentialShared;
    }
}
catch
{ /* non-fatal */
}

// Add API services
builder.Services.AddPrintFarmerControllers();

builder.Services.AddEndpointsApiExplorer();

// .NET 10 native OpenAPI - auto-detects JWT Bearer security from authentication configuration
builder.Services.AddOpenApi();

// CORS configuration for API access
builder.Services.AddPrintFarmerCors();

// TODO: Simple rate limiting scaffold for OctoPrint endpoints - implementation pending
// NOTE: This is a lightweight scaffold; replace with production-ready rate limiter if needed
// builder.Services.AddSingleton<Farm.Web.Api.Middleware.SimpleRateLimitService>();

// Configure OpenTelemetry (skippable for tests)
builder.Services.AddPrintFarmerTelemetry(builder.Configuration, builder.Environment);

// SignalR for real-time updates
builder.Services.AddPrintFarmerSignalR();

// Health checks
builder.Services.AddPrintFarmerHealthChecks();

// Validation
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Feature services (OctoPrint, File Management, Print Jobs, Maintenance, SPA)
builder.Services.AddPrintFarmerFeatureServices(builder.Configuration, builder.Environment);

// Register background services for distributed slicing
builder.Services.AddPrintFarmerBackgroundServices(builder.Configuration);

// Add JWT Authentication and Authorization
builder.Services.AddPrintFarmerAuthentication(builder.Configuration, builder.Environment);

// Bind (HTTP) to configured dev port; using launchSettings.json for default. Override via ASPNETCORE_URLS if needed.
#pragma warning disable S1075 // URIs should not be hardcoded
// Only bind HTTP listener in non-testing environments. When running integration
// tests via WebApplicationFactory/TestServer the test host provides its own
// in-memory server; calling UseUrls here can interfere with TestServer and
// result in "server has not been started" errors in CreateClient().
try
{
    string? envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    if (!string.Equals(envName, "Testing", StringComparison.OrdinalIgnoreCase))
    {
        _ = builder.WebHost.UseUrls("http://0.0.0.0:5245");
    }
}
catch
{
    // Best-effort; do not fail startup if env APIs are not available in some hosts
}
#pragma warning restore S1075 // URIs should not be hardcoded
// Capture a few startup/root services from the service collection before building the
// final application service provider. This avoids sprinkling `app.Services.GetService`
// callsites around `Program.cs` while still allowing top-level initialization to
// use the services safely. We build a temporary provider (disposed immediately)
// and stash references to services that are safe to keep for the lifetime of the
// process (loggers, unified logging, temp path provider, startup status).
IUnifiedLoggingService? _capturedStartupUnifiedLogging = null;
ILogger<Program>? _capturedStartupLogger = null;
ITempPathProvider? _capturedTempPathProvider = null;
IStartupStatus? _capturedStartupStatus = null;

WebApplication app;
try
{
    app = builder.Build();
}
catch (Exception ex)
{
    try
    {
        string? envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(envName, "Testing", StringComparison.OrdinalIgnoreCase) || string.Equals(Environment.GetEnvironmentVariable("DISABLE_TELEMETRY"), "true", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable CA1303
            Console.WriteLine("Program.cs: Build() threw during test startup:");
            Console.WriteLine(ex.ToString());
#pragma warning restore CA1303
        }
    }
    catch
    {
    }

    throw;
}

// Populate previously-deferred startup captures using the built application service provider.
// Use CreateAsyncScope to resolve scoped/singleton services safely without calling BuildServiceProvider on the service collection.
try
{
    await using AsyncServiceScope _captureScope = app.Services.CreateAsyncScope();
    IServiceProvider _captureSp = _captureScope.ServiceProvider;
    _capturedStartupUnifiedLogging = _captureSp.GetService<IUnifiedLoggingService>();
    _capturedStartupLogger = _captureSp.GetService<ILogger<Program>>();
    _capturedTempPathProvider = _captureSp.GetService<ITempPathProvider>();
    _capturedStartupStatus = _captureSp.GetService<IStartupStatus>();
}
catch
{
    // If capture fails, leave captured variables null and fall back to app-level resolution later.
}

// Configure artifact storage metrics thresholds and alerts
try
{
    ArtifactStorageSettings artifactSettings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArtifactStorageSettings>>().Value;
    ArtifactsMetrics artifactMetrics = app.Services.GetRequiredService<ArtifactsMetrics>();

    if (artifactSettings.EnableStorageAlerts)
    {
        artifactMetrics.SetThresholds(artifactSettings.StorageWarningThresholdBytes, artifactSettings.StorageCriticalThresholdBytes);

        // Subscribe to threshold events for logging
        artifactMetrics.ThresholdExceeded += (sender, e) =>
        {
            ILogger<Program>? logger = app.Services.GetService<ILogger<Program>>();
            string levelStr = e.Level switch
            {
                Farm.Web.Api.Services.Artifacts.StorageThresholdLevel.Warning => "WARNING",
                Farm.Web.Api.Services.Artifacts.StorageThresholdLevel.Critical => "CRITICAL",
                _ => "UNKNOWN"
            };

            logger?.LogWarning(
                "[ArtifactStorage] {Level} threshold exceeded: {CurrentGB:F2} GB (Warning: {WarningGB:F2} GB, Critical: {CriticalGB:F2} GB)",
                levelStr,
                e.CurrentBytes / (1024.0 * 1024 * 1024),
                e.WarningThreshold / (1024.0 * 1024 * 1024),
                e.CriticalThreshold / (1024.0 * 1024 * 1024));
        };
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "[Startup] Failed to configure artifact storage thresholds");
}

// NOTE: Settings initialization from environment variables is performed
// during database initialization in ProgramHelpers.InitializeDatabaseAsync
// to ensure the database schema exists before any SettingsService queries run.

// Early liveness endpoint (process up) + readiness separate
app.MapGet("/livez", () => Results.Ok(new { status = "alive" }));

// Deferred console redirection (avoids blocking early host binding). Enable via ENABLE_CONSOLE_REDIRECTION=true
if (string.Equals(Environment.GetEnvironmentVariable("ENABLE_CONSOLE_REDIRECTION"), "true", StringComparison.OrdinalIgnoreCase))
{
    IHostApplicationLifetime lifetime = app.Lifetime; // IHostApplicationLifetime

    // Capture root-level logging services once to avoid per-call scope creation inside the callback
    // Prefer startup-captured unified logging / logger when available to avoid creating
    // a scope inside the ApplicationStarted callback.
    IUnifiedLoggingService? _deferredUls = _capturedStartupUnifiedLogging ?? app.Services.GetService<IUnifiedLoggingService>();
    ILogger<Program>? _deferredLg = _capturedStartupLogger ?? app.Services.GetService<ILogger<Program>>();

    _ = lifetime.ApplicationStarted.Register(() => ProgramHelpers.HandleDeferredConsoleRedirection(_deferredUls, _deferredLg));
}

// Handle CLI commands (exits if command processed)
if (await app.HandleCliCommandsAsync(args))
{
    return;
}

// Log effective temp root (non-production) for diagnostics
try
{
    // Prefer app.Environment (already available) instead of resolving IHostEnvironment from service provider
    if (!app.Environment.IsProduction())
    {
        ITempPathProvider? tempProvider = _capturedTempPathProvider ?? app.Services.GetService<ITempPathProvider>();
        if (tempProvider != null)
        {
            app.Logger.LogInformation("[Startup] Temp root: {TempRoot}", tempProvider.GetTempRoot());
        }
        else
        {
            app.Logger.LogInformation("[Startup] Temp root: <no provider registered>");
        }
    }
}
catch
{ /* ignore diagnostics failure */
}

// === MIDDLEWARE PIPELINE ===

// Global exception handling
app.UseMiddleware<GlobalExceptionMiddleware>();

// Add telemetry middleware early in the pipeline
app.UseTelemetryMiddleware();

if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
}

// Native ASP.NET Core OpenAPI automatically exposes at /openapi/v1.json
app.UseCors("Default");

// Rate limiting for authentication endpoints
app.UseMiddleware<AuthenticationRateLimitMiddleware>();

// Authentication and Authorization
app.UseAuthentication();
app.UseAuthorization();

// Configure API routing and SignalR hubs
app.MapControllers();
app.MapHub<PrinterHub>("/hubs/printers");
app.MapHub<HarvestHub>("/hubs/harvest");
app.MapHub<MaintenanceHub>("/hubs/maintenance");

// Slicer hubs (registry + progress) from slicer module
app.MapSlicerHubs();

// Prometheus metrics endpoint (guarded so tests without MeterProvider don't throw)
try
{
    if (app.Services.GetService<MeterProvider>() != null)
    {
        _ = app.MapPrometheusScrapingEndpoint();
    }
}
catch
{
    // In minimal test environments MeterProvider may be absent; skip exposing metrics
}

// Health checks
// Capture host environment and resolve startup status from the root service provider (app.Services)
// Use app.Environment directly instead of resolving IHostEnvironment from the service provider
IHostEnvironment _programHostEnvironment = app.Environment;

// Resolve IStartupStatus once from the root provider (it's a singleton-like service used for diagnostics)
IStartupStatus? _startupStatus = _capturedStartupStatus ?? app.Services.GetService<IStartupStatus>();
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        await ProgramHelpers.WriteHealthResponseAsync(context, report, _startupStatus, _programHostEnvironment);
    }
});

// Alias route for clients expecting the comprehensive health endpoint under /api prefix
app.MapHealthChecks("/api/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        await ProgramHelpers.WriteHealthResponseAsync(context, report, _startupStatus, _programHostEnvironment);
    }
});

// Minimal API for network discovery settings
// Helper: Map between model and DTO

// Network discovery settings now available via UnifiedSettingsController:
// GET /api/settings/network-discovery
// POST /api/settings/network-discovery
app.MapPost("/api/network-discovery/settings/validate", [Authorize(Policy = "RequireAdmin")] ([FromBody] NetworkDiscoverySettings body) =>
{
    NetworkValidationResult validation = NetworkValidationService.ValidateSettings(body);
    return Results.Ok(new
    {
        isValid = validation.IsValid,
        errors = validation.Errors,
        warnings = validation.Warnings,
        suggestions = validation.Suggestions
    });
});

// SignalR settings now available via UnifiedSettingsController:
// GET /api/settings/signalr
// POST /api/settings/signalr
app.MapPost("/api/network-discovery/auto-detect", [Authorize(Policy = "RequireAdmin")] () => ProgramHelpers.AutoDetectNetworkRanges());
app.MapPost("/api/network-discovery/settings/apply-env", [Authorize(Policy = "RequireAdmin")] ([FromServices] ISettingsService settingsService) =>
{
    // Allows re-applying environment driven defaults from DISCOVERY_RANGES / DISCOVERY_PORTS
    string? rangesEnv = Environment.GetEnvironmentVariable("DISCOVERY_RANGES");
    string? portsEnv = Environment.GetEnvironmentVariable("DISCOVERY_PORTS");
    NetworkDiscoverySettings current = settingsService.Get<NetworkDiscoverySettings>() ?? new NetworkDiscoverySettings();

    // TODO: Update logic for new NetworkDiscoverySettings properties if needed
    settingsService.Save(current);
    return Results.Ok(current);
});

// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Extended diagnostic: expose active temp root (non-sensitive path) for debugging; omit if running in Production

// Final log just before entering host run loop (diagnostic)
app.Logger.LogInformation("[Startup] Reached app.Run() - binding to configured URLs");

// Database info endpoint (dev or DEBUG_DB_INFO=true) with migration status integration.
app.MapGet("/api/debug/db-info", async (
    AppDbContext db,
    IWebHostEnvironment env,
    IConfiguration config,
    [FromServices] IMigrationStatusProvider migrationStatusProvider,
    CancellationToken ct) =>
{
    string? toggle = (Environment.GetEnvironmentVariable("DEBUG_DB_INFO") ?? config["DEBUG_DB_INFO"])?.Trim();
    bool allow = env.IsDevelopment() || (toggle != null && toggle.Equals("true", StringComparison.OrdinalIgnoreCase));
    if (!allow)
    {
        return Results.NotFound();
    }

    string provider = db.Database.ProviderName ?? "unknown";
    string databaseName;
    try
    {
        databaseName = db.Database.GetDbConnection().Database;
    }
    catch
    {
        databaseName = "unknown";
    }

    Dictionary<string, int> entities = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(db.Printers)] = await db.Printers.CountAsync(ct),
        [nameof(db.Spools)] = await db.Spools.CountAsync(ct),
        [nameof(db.Manufacturers)] = await db.Manufacturers.CountAsync(ct),
        [nameof(db.PrinterModels)] = await db.PrinterModels.CountAsync(ct),
        [nameof(db.FilamentTypes)] = await db.FilamentTypes.CountAsync(ct),
        [nameof(db.SpoolmanConfigs)] = await db.SpoolmanConfigs.CountAsync(ct),
        [nameof(db.GcodeFiles)] = await db.GcodeFiles.CountAsync(ct),
        [nameof(db.PrintJobs)] = await db.PrintJobs.CountAsync(ct),
        [nameof(db.GcodeHarvestOperations)] = await db.GcodeHarvestOperations.CountAsync(ct),
        [nameof(db.HarvestDiscoveredFiles)] = await db.HarvestDiscoveredFiles.CountAsync(ct),
        [nameof(db.Users)] = await db.Users.CountAsync(ct),
        [nameof(db.Roles)] = await db.Roles.CountAsync(ct),
        [nameof(db.Resources)] = await db.Resources.CountAsync(ct),

        // Actions removed - old authorization entity
        [nameof(db.RolePermissions)] = await db.RolePermissions.CountAsync(ct),
        [nameof(db.UserRoles)] = await db.UserRoles.CountAsync(ct)
    };

    long? fileSizeBytes = null;
    if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            string cs = db.Database.GetConnectionString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cs))
            {
                SqliteConnectionStringBuilder builder = new(cs);
                string dataSource = builder.DataSource;

                // CA3003: dataSource is from connection string, not user input
#pragma warning disable CA3003
                if (!Path.IsPathRooted(dataSource))
                {
                    dataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataSource));
                }

                if (File.Exists(dataSource))
                {
                    fileSizeBytes = new System.IO.FileInfo(dataSource).Length;
                }
#pragma warning restore CA3003
            }
        }
        catch
        {
        }
    }

    MigrationStatus migration = migrationStatusProvider.GetStatus();

    return Results.Ok(new
    {
        provider,
        database = databaseName,
        timestampUtc = DateTime.UtcNow,
        fileSizeBytes,
        migration = new { migration.Mode, migration.HasMigrations, migration.AppliedAny },
        entities
    });
});

// Configure SPA only for monolithic deployments (not microservices)
bool isMonolithicDeployment = builder.Configuration.GetValue<string>("DEPLOYMENT_MODE") != "microservices";
if (isMonolithicDeployment)
{
    // Only enable static file / SPA pipeline if a web root actually exists (prebuilt assets). In container builds
    // using DEPLOYMENT_MODE=monolithic we expect /wwwroot to be present; if it's missing we skip to avoid crashes.
    string staticRoot = app.Environment.WebRootPath;
    if (!string.IsNullOrWhiteSpace(staticRoot) && Directory.Exists(staticRoot))
    {
        _ = app.UseStaticFiles();

        if (app.Environment.IsDevelopment())
        {
            // Dynamic proxy middleware will handle forwarding once dev server becomes available
            _ = app.UseMiddleware<SpaDynamicProxyMiddleware>();
        }
        else
        {
            // Production: serve pre-built SPA assets (only if root present)
            app.UseSpa(spa =>
            {
                spa.Options.SourcePath = "wwwroot";
                spa.Options.DefaultPageStaticFileOptions = new StaticFileOptions
                {
                    OnPrepareResponse = ctx =>
                    {
                        ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
                        ctx.Context.Response.Headers.Append("Pragma", "no-cache");
                        ctx.Context.Response.Headers.Append("Expires", "0");
                    }
                };
            });
        }
    }
    else
    {
        app.Logger.LogWarning("[Startup][SPA] Skipping SPA static file pipeline: WebRootPath missing or directory not found: {WebRootPath}", staticRoot);
    }
}

// Configure static file serving for artifacts if enabled
try
{
    ArtifactStorageSettings artifactSettings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArtifactStorageSettings>>().Value;
    if (artifactSettings.EnableStaticServing)
    {
        string artifactPath = Path.IsPathRooted(artifactSettings.RootPath)
            ? artifactSettings.RootPath
            : Path.Combine(app.Environment.ContentRootPath, artifactSettings.RootPath);

        if (Directory.Exists(artifactPath))
        {
            _ = app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(artifactPath),
                RequestPath = "/artifacts",
                OnPrepareResponse = ctx =>
                {
                    // Cache artifacts for 1 hour (they are immutable once created)
                    ctx.Context.Response.Headers.Append("Cache-Control", "public, max-age=3600");
                },
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream"
            });
            app.Logger.LogInformation("[Startup] Artifact static serving enabled at /artifacts (path: {Path})", artifactPath);
        }
        else
        {
            app.Logger.LogWarning("[Startup] Artifact static serving enabled but path does not exist: {Path}", artifactPath);
        }
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "[Startup] Failed to configure artifact static file serving");
}

// Initialize database (ensures schema exists before resolving SettingsService)
// Always run in all environments including Testing so integration tests have schema & seed data available.
try
{
    await ProgramHelpers.InitializeDatabaseAsync(app);
}
catch (Exception ex)
{
    // Emit diagnostic details in Testing environment but still surface the failure to the test host.
    try
    {
        if (app.Environment.IsEnvironment("Testing") || string.Equals(Environment.GetEnvironmentVariable("DISABLE_TELEMETRY"), "true", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable CA1303
            Console.WriteLine("Program.cs: InitializeDatabaseAsync threw during test startup:");
            Console.WriteLine(ex.ToString());
#pragma warning restore CA1303
        }
    }
    catch
    {
    }

    throw;
}

// Ensure storage directories exist (creates gcode, models, profiles directories if they don't exist)
try
{
    await using AsyncServiceScope storageScope = app.Services.CreateAsyncScope();
    IStoragePathService storagePathService = storageScope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>();
    await storagePathService.EnsureDirectoriesExistAsync();
}
catch (Exception ex)
{
    _capturedStartupUnifiedLogging?.LogError(ex, "Failed to ensure storage directories exist");
    throw;
}

// In test environments the test host (WebApplicationFactory/TestServer) manages the server lifecycle.
// Avoid calling RunAsync when running under the 'Testing' environment to prevent interfering with the test host.
if (!app.Environment.IsEnvironment("Testing"))
{
    await app.RunAsync();
}
else
{
    // For integration tests we still need the server pipeline configured so TestServer can dispatch requests.
    // Start the app without blocking the test host.
    try
    {
        await app.StartAsync();
#pragma warning disable CA1303
        Console.WriteLine("Program.cs: Started app for Testing environment (non-blocking StartAsync)");
#pragma warning restore CA1303
    }
    catch (Exception ex)
    {
#pragma warning disable CA1303
        Console.WriteLine("Program.cs: StartAsync failed in Testing environment: " + ex.Message);
#pragma warning restore CA1303
        throw;
    }
}

// Expose Program for WebApplicationFactory in tests
[SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable", Justification = "Public partial Program required for WebApplicationFactory in tests and minimal hosting model.")]
public partial class Program
{
    // Cached JSON options to avoid per-call allocations (CA1869)
    public static readonly JsonSerializerOptions HealthJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    protected Program()
    {
    }
}

// Small test-only startup filter used from Program when running under Testing
