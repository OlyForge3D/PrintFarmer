using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.FeatureFlags;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.Abstractions;
using Farm.Slicer.Integration;
using Farm.Web.Api;
using Farm.Web.Api.Authorization;
using Farm.Web.Api.Health;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Infrastructure.OpenApi;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Startup;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

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

// Forwarded headers (X-Forwarded-For / -Proto / -Host) — bind config + options.
// Disabled by default; operators behind a reverse proxy must set
// ForwardedHeaders:Enabled=true and populate KnownProxies / KnownNetworks.
// See docs/DEPLOYMENT.md and the ForwardedHeaders section in appsettings.json.
builder.Services.AddPrintFarmerForwardedHeaders(builder.Configuration);

// Feature flag service for phased rollout control
builder.Services.AddSingleton<IFeatureFlagService, FeatureFlagService>();

// Slicer integration shim: loads Farm.Slicer.Module + Farm.Slicer.Module.Api DLLs at runtime
// from Slicer:PluginsPath. No compile-time reference to EF Core, SignalR hubs, or OrcaSlicer.
// All slicer registrations delegated to runtime-discovered ISlicerModule implementations.
// In microservices mode, the slicer module runs in a separate slicer-host process —
// the API does not load slicer DLLs, but the platform still reports slicing as available
// so the frontend can route requests to the slicer-host via nginx.
string? deployType = builder.Configuration.GetValue<string>("DEPLOYMENT_MODE")
                   ?? builder.Configuration.GetValue<string>("DEPLOYMENT_TYPE");
bool isMicroservices = deployType == "microservices";
bool slicerModuleEnabled = !isMicroservices;

// Platform-aware capability checks: auto-disable native x86-only features on ARM64 (Raspberry Pi)
// unless explicitly overridden via configuration. Affects lib3mf, AssimpNetter, and slicer integration.
var arch = RuntimeInformation.ProcessArchitecture;
bool isArm = arch is Architecture.Arm64 or Architecture.Arm;
bool modelFilesEnabled = builder.Configuration.GetValue("Platform:ModelFilesEnabled", true);
bool thumbnailEnabled = builder.Configuration.GetValue("Platform:ThumbnailGenerationEnabled", true);

// slicerEnabled = platform capability flag reported to the frontend.
// In microservices mode this starts as true (slicer-host provides the service).
bool slicerEnabled = true;

if (isArm)
{
    var modelFilesExplicit = builder.Configuration.GetSection("Platform:ModelFilesEnabled").Value;
    var slicerExplicit = builder.Configuration.GetSection("Slicer:Enabled").Value;
    var thumbnailExplicit = builder.Configuration.GetSection("Platform:ThumbnailGenerationEnabled").Value;

    if (modelFilesExplicit is null)
    {
        modelFilesEnabled = false;
    }

    if (slicerExplicit is null)
    {
        slicerEnabled = false;
        slicerModuleEnabled = false;
    }

    if (thumbnailExplicit is null)
    {
        thumbnailEnabled = false;
    }
}
else
{
    // On x86/x64, respect configuration flags
    bool slicerConfigEnabled = builder.Configuration.GetValue("Slicer:Enabled", true);
    slicerEnabled = slicerConfigEnabled;
    slicerModuleEnabled = slicerModuleEnabled && slicerConfigEnabled;
    modelFilesEnabled = builder.Configuration.GetValue("Platform:ModelFilesEnabled", true);
}

// Write resolved capability values back to configuration so downstream consumers
// (e.g. SystemCapabilitiesController) read the single source of truth.
builder.Configuration["Slicer:Enabled"] = slicerEnabled.ToString();
builder.Configuration["Platform:ModelFilesEnabled"] = modelFilesEnabled.ToString();
builder.Configuration["Platform:ThumbnailGenerationEnabled"] = thumbnailEnabled.ToString();

// When slicer is disabled, cross-module consumers use = null default parameter values.
// .NET DI's ActivatorUtilities skips unregistered services that have default values.

// Register SystemLog logger provider to capture warnings and errors to the database.
// Using Warning level to avoid flooding PostgreSQL with high-volume Information logs
// (at Information level the 146M-row SystemLogs table caused severe I/O contention).
builder.Logging.AddSystemLogProvider(LogLevel.Warning);

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

// Add API services (returns mvcBuilder so the slicer integration shim can add ApplicationParts)
IMvcBuilder mvcBuilder = builder.Services.AddPrintFarmerControllers();

// Vertical-slice API module discovery/registration seam (issue #2035, epic #2019). Discovery
// only scans assemblies explicitly listed here. Farm.Modules.SmartPlug is the pilot module
// (issue #2036) -- the first assembly to move a controller + services out of the monolith.
// Farm.Modules.PrintQueue (issue #2040, Phase 12) is the second, and
// Farm.Modules.Calibration (issue #2038, Phase 10) is the third.
// Farm.Modules.Maintenance (issue #2037) is the first to also move a SignalR hub -- see its
// MapEndpoints for the MapHub<MaintenanceHub> call that used to live here.
// Farm.Modules.Devices (issue #2043, Phase 15) is the fifth -- OctoPrint API-key
// authentication, NFC, and cameras (the admin Home Assistant controller had already moved to
// Farm.Modules.Administration by the time this phase merged -- see Phase 14, issue #2042).
// Farm.Modules.Gcode (issue #2039, Phase 11) is the sixth -- Gcode/harvest/promotion.
// Farm.Modules.Identity (issue #2041, Phase 13) is the seventh -- user/role/auth
// management moved out of the monolith.
// Farm.Modules.Inventory (issue #2044, Phase 16) is the eighth -- adds the inventory
// controller slice with no host-wide DI moves.
// Farm.Modules.Administration (issue #2042, Phase 14) is the ninth -- Admin Control Center
// overview, data export/import, Home Assistant, Telegram, and settings controllers.
builder.Services.AddApiModules(
    mvcBuilder,
    builder.Configuration,
    typeof(Farm.Modules.SmartPlug.SmartPlugApiModule).Assembly,
    typeof(Farm.Modules.PrintQueue.PrintQueueApiModule).Assembly,
    typeof(Farm.Modules.Maintenance.MaintenanceApiModule).Assembly,
    typeof(Farm.Modules.Calibration.CalibrationApiModule).Assembly,
    typeof(Farm.Modules.Devices.DevicesApiModule).Assembly,
    typeof(Farm.Modules.Gcode.GcodeApiModule).Assembly,
    typeof(Farm.Modules.Identity.IdentityApiModule).Assembly,
    typeof(Farm.Modules.Inventory.InventoryApiModule).Assembly,
    typeof(Farm.Modules.Administration.AdministrationApiModule).Assembly);

if (slicerModuleEnabled)
{
    // Load slicer DLLs, register their services, and add their controllers as ApplicationParts.
    builder.Services.AddSlicerIntegration(mvcBuilder, builder.Configuration);
    builder.Services.AddSlicerHostAdapters();
}

// MoonrakerEmulatorSeeder runs on the API host in every deployment topology and needs the
// machine/process/filament profile repositories. Split/microservices hosts skip AddSlicerModule,
// so register just those repositories here (issue #1858).
builder.Services.AddMoonrakerEmulatorSeederDependencies(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();

// Native OpenAPI builds schemas from Http.Json metadata. MVC controller serialization is configured separately.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver = (options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
        .WithAddedModifier(PruneOpenApiMaintenanceNavigationProperties);
});

// .NET 10 native OpenAPI - auto-detects JWT Bearer security from authentication configuration
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    options.AddOperationTransformer<AuthorizationOperationTransformer>();
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.MaxDepth = 256);

// CORS configuration for API access
builder.Services.AddPrintFarmerCors();

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

// Artifact -> GcodeFile promotion (GcodePromotionReconcilerState, IGcodeArtifactPromoter,
// GcodePromotionReconciliationService) is now registered by Farm.Modules.Gcode's
// GcodeApiModule (issue #2039, epic #2019).

// Register background services for distributed slicing
builder.Services.AddPrintFarmerBackgroundServices(builder.Configuration);

// Add JWT Authentication and Authorization
builder.Services.AddPrintFarmerAuthentication(builder.Configuration, builder.Environment);

// Use the canonical local-development port only when the host has no explicit URL binding.
// Container images set ASPNETCORE_URLS and must retain that deployment-owned binding.
ProgramHelpers.ConfigureDefaultHttpUrl(builder);

// Capture a few startup/root services from the service collection before building the
// final application service provider. This avoids sprinkling `app.Services.GetService`
// callsites around `Program.cs` while still allowing top-level initialization to
// use the services safely. We build a temporary provider (disposed immediately)
// and stash references to services that are safe to keep for the lifetime of the
// process (loggers, unified logging, temp path provider, startup status).
ILogger<Program>? _capturedStartupUnifiedLogging = null;
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
        if (builder.Environment.IsEnvironment("Testing")
            || string.Equals(builder.Configuration["DISABLE_TELEMETRY"], "true", StringComparison.OrdinalIgnoreCase))
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

bool devModeBypassRequested = app.Configuration.GetValue<bool>(DevModeAuthorizationHandler.ConfigurationKey);
if (devModeBypassRequested && app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "Development-mode authorization bypass is ENABLED. Unauthenticated safe-method requests will bypass authorization.");
}
else if (devModeBypassRequested)
{
    app.Logger.LogWarning(
        "Development-mode authorization bypass was requested but is disabled outside Development. Current environment: {EnvironmentName}.",
        app.Environment.EnvironmentName);
}
else
{
    app.Logger.LogInformation("Development-mode authorization bypass is disabled.");
}

// Populate previously-deferred startup captures using the built application service provider.
// Use CreateAsyncScope to resolve scoped/singleton services safely without calling BuildServiceProvider on the service collection.
try
{
    await using AsyncServiceScope _captureScope = app.Services.CreateAsyncScope();
    IServiceProvider _captureSp = _captureScope.ServiceProvider;
    _capturedStartupUnifiedLogging = _captureSp.GetService<ILogger<Program>>();
    _capturedStartupLogger = _captureSp.GetService<ILogger<Program>>();
    _capturedTempPathProvider = _captureSp.GetService<ITempPathProvider>();
    _capturedStartupStatus = _captureSp.GetService<IStartupStatus>();
}
catch
{
    // If capture fails, leave captured variables null and fall back to app-level resolution later.
}

// Log platform capabilities for startup diagnostics
if (isArm && (!modelFilesEnabled || !slicerEnabled))
{
    app.Logger.LogWarning("ARM platform detected ({Architecture}) — 3D model files and/or slicing features disabled", arch);
}

app.Logger.LogInformation(
    "Platform capabilities: Architecture={Architecture}, SlicingEnabled={SlicingEnabled}, SlicerModuleLoaded={SlicerModuleLoaded}, ModelFilesEnabled={ModelFilesEnabled}",
    arch,
    slicerEnabled,
    slicerModuleEnabled,
    modelFilesEnabled);

// Forwarded headers must be the FIRST middleware in the pipeline so that
// Connection.RemoteIpAddress is rewritten (or intentionally left as the direct
// peer) before ANY other middleware — including runtime-discovered slicer plugin
// modules — has a chance to read it. Registering here (before UseSlicerIntegration)
// guarantees that even if a plugin module calls app.Use(...) in its Configure()
// hook, it cannot register middleware ahead of the trust gate. No-op unless
// ForwardedHeaders:Enabled=true and trusted proxies are configured. (#862)
app.UsePrintFarmerForwardedHeaders();

// Post-build slicer module configuration (metrics thresholds, alert subscriptions, etc.)
if (slicerModuleEnabled)
{
    app.UseSlicerIntegration();
}

// NOTE: Settings initialization from environment variables is performed
// during database initialization in ProgramHelpers.InitializeDatabaseAsync
// to ensure the database schema exists before any SettingsService queries run.

// Public because orchestrator liveness probes cannot obtain application credentials.
app.MapGet("/livez", () => Results.Ok(new { status = "alive" }))
    .AllowAnonymous();

// Deferred console redirection (avoids blocking early host binding). Enable via ENABLE_CONSOLE_REDIRECTION=true
if (string.Equals(Environment.GetEnvironmentVariable("ENABLE_CONSOLE_REDIRECTION"), "true", StringComparison.OrdinalIgnoreCase))
{
    IHostApplicationLifetime lifetime = app.Lifetime; // IHostApplicationLifetime

    // Capture root-level logging services once to avoid per-call scope creation inside the callback
    // Prefer startup-captured unified logging / logger when available to avoid creating
    // a scope inside the ApplicationStarted callback.
    ILogger<Program>? _deferredUls = _capturedStartupUnifiedLogging ?? app.Services.GetService<ILogger<Program>>();
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
// NOTE: UsePrintFarmerForwardedHeaders is registered earlier (before slicer
// integration) so that plugin-registered middleware cannot precede the trust
// gate. See the #862 comment above.

// Global exception handling
app.UseMiddleware<GlobalExceptionMiddleware>();

// Add telemetry middleware early in the pipeline
app.UseTelemetryMiddleware();

if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi().AllowAnonymous(); // Public only in Development for pre-login API tooling.
}

// Native ASP.NET Core OpenAPI automatically exposes at /openapi/v1.json
app.UseCors("Default");

// Configure static file serving for monolith deployment mode
// This must come BEFORE authentication so static files are served without auth
// When DEPLOYMENT_MODE=monolith, the API serves the React frontend directly from wwwroot
// When DEPLOYMENT_MODE=microservices (or unset), frontend is served by separate nginx-proxy container
string? deploymentMode = builder.Configuration.GetValue<string>("DEPLOYMENT_MODE")
    ?? builder.Configuration.GetValue<string>("Deployment:Mode")
    ?? Environment.GetEnvironmentVariable("DEPLOYMENT_MODE");
bool isMonolithMode = string.Equals(deploymentMode, "monolith", StringComparison.OrdinalIgnoreCase);

if (isMonolithMode)
{
    // Monolith mode: API serves the React frontend static files
    string staticRoot = app.Environment.WebRootPath;
    if (!string.IsNullOrWhiteSpace(staticRoot) && Directory.Exists(staticRoot))
    {
        app.Logger.LogInformation("[Startup] Running in monolith mode — serving frontend from wwwroot/");

        // Serve static files from wwwroot (JS, CSS, images, etc.)
        // This comes before authentication so static assets are publicly accessible
        _ = app.UseStaticFiles();

        if (app.Environment.IsDevelopment())
        {
            // Development: proxy to Vite dev server (if available)
            _ = app.UseMiddleware<SpaDynamicProxyMiddleware>();
        }
    }
    else
    {
        app.Logger.LogWarning("[Startup][Monolith] wwwroot directory not found at {WebRootPath} — static file serving disabled", staticRoot);
    }
}
else
{
    // Microservices mode (default): frontend served by separate nginx-proxy container
    // CORS is needed because frontend and API are on different origins
    app.Logger.LogInformation("[Startup] Running in microservices mode — frontend served externally");
}

// Rate limiting for authentication endpoints
app.UseMiddleware<AuthenticationRateLimitMiddleware>();

// Authentication and Authorization
app.UseAuthentication();
app.UseAuthorization();

// Configure API routing and SignalR hubs
app.MapControllers();

// Map endpoints for any discovered IApiModule (issue #2035, epic #2019). Farm.Modules.SmartPlug
// has no minimal-API endpoints of its own -- its controller is routed via MapControllers() --
// so this remains effectively a no-op for that module until a later phase adds one that needs it.
app.MapApiModules();

// Public farm hubs require authenticated clients.
app.MapHub<PrinterHub>("/hubs/printers");
app.MapHub<HarvestHub>("/hubs/harvest");

// MaintenanceHub is now mapped by MaintenanceApiModule.MapEndpoints() via MapApiModules()
// above -- see Farm.Modules.Maintenance (issue #2037).

// Requires an authenticated user — broadcasts carry farm-wide spool and printer identifiers.
// NFC firmware never connects to this hub; devices ingest scans via a separate REST path
// (NfcDevicesController) authenticated with X-Nfc-Device-Token.
app.MapHub<NfcHub>("/hubs/nfc");

// Slicer hubs (registry + progress): delegated to runtime-loaded ISlicerHubRegistrar
if (slicerModuleEnabled)
{
    app.MapSlicerIntegrationHubs();
}
else
{
    // When slicer module is not loaded in this process (e.g. microservices mode),
    // the slicer controllers are absent. Map stub endpoints for list routes the
    // frontend expects (empty results instead of 404s) and a catch-all for all
    // other slicer routes.
    // In microservices mode, nginx routes slicer paths to the slicer-host,
    // so these stubs are only hit if nginx misconfiguration falls through to the API.
    app.MapGet("/api/3d-models", () => Results.Ok(Array.Empty<object>()))
        .RequireAuthorization();
    app.MapGet("/api/3d-models/folders", () => Results.Ok(Array.Empty<object>()))
        .RequireAuthorization();
    app.MapPost("/api/3d-models/query", () => Results.Ok(Array.Empty<object>()))
        .RequireAuthorization();

    // Catch-all for all remaining slicer API routes when module is disabled.
    // Returns a structured 404 so the frontend can display "Slicing not available".
    // Skips requests that already matched a stub endpoint above (GetEndpoint != null).
    string[] slicerPrefixes = ["/api/3d-models/", "/api/slicer", "/api/slicers", "/api/workers", "/api/artifacts", "/api/slice", "/api/admin/slicer"];
    app.Use(async (context, next) =>
    {
        if (context.GetEndpoint() != null)
        {
            await next();
            return;
        }

        string path = context.Request.Path.Value ?? string.Empty;
        bool isSlicerRoute = Array.Exists(slicerPrefixes, prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (isSlicerRoute)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Slicing module is not enabled", code = "SLICER_DISABLED" });
            return;
        }

        await next();
    });
}

// Prometheus metrics endpoint (guarded so tests without MeterProvider don't throw)
try
{
    if (app.Services.GetService<MeterProvider>() != null)
    {
        _ = app.MapPrometheusScrapingEndpoint().AllowAnonymous(); // Public for network-policy-authenticated collectors.
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

// Public because Docker and orchestrator health checks cannot obtain application credentials.
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        await ProgramHelpers.WriteHealthResponseAsync(context, report, _startupStatus, _programHostEnvironment);
    }
}).AllowAnonymous();

// Public for compatibility with health-check clients that use the /api-prefixed route.
app.MapHealthChecks("/api/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        await ProgramHelpers.WriteHealthResponseAsync(context, report, _startupStatus, _programHostEnvironment);
    }
}).AllowAnonymous();

// Minimal API for network discovery settings
// Helper: Map between model and DTO

// Network discovery settings now available via UnifiedSettingsController:
// GET /api/settings/network-discovery
// POST /api/settings/network-discovery
app.MapPost("/api/network-discovery/settings/validate", [RequirePermission("network_discovery", "admin")] ([FromBody] NetworkDiscoverySettings body) =>
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
app.MapPost("/api/network-discovery/auto-detect", [RequirePermission("network_discovery", "admin")] () => ProgramHelpers.AutoDetectNetworkRanges());
app.MapPost("/api/network-discovery/settings/apply-env", [RequirePermission("network_discovery", "admin")] ([FromServices] ISettingsService settingsService) =>
{
    // Allows re-applying environment driven defaults from DISCOVERY_RANGES / DISCOVERY_PORTS
    string? rangesEnv = Environment.GetEnvironmentVariable("DISCOVERY_RANGES");
    string? portsEnv = Environment.GetEnvironmentVariable("DISCOVERY_PORTS");
    NetworkDiscoverySettings current = settingsService.Get<NetworkDiscoverySettings>() ?? new NetworkDiscoverySettings();

    if (!string.IsNullOrWhiteSpace(rangesEnv))
    {
        current.DiscoverySubnets = rangesEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    settingsService.Save(current);
    return Results.Ok(current);
});

// Public because login screens and basic uptime probes use this endpoint before authentication.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

// Public because login and update clients need build compatibility metadata before authentication.
app.MapGet("/api/system/version", () =>
{
    (string version, string? commit) = Farm.Web.Api.Health.BuildVersion.FromAssembly();

    return Results.Ok(new
    {
        service = "Farm.Web.Api",
        version,
        commit,
        environment = app.Environment.EnvironmentName,
        runtime = RuntimeInformation.FrameworkDescription,
        timestamp = DateTime.UtcNow,
    });
}).AllowAnonymous();

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

// SPA fallback for monolith mode: serve index.html for client-side routing
// This must come AFTER all Map* calls so API routes take priority
// MapFallbackToFile automatically excludes /api/*, /hubs/*, health endpoints, and existing static files
if (isMonolithMode)
{
    string staticRoot = app.Environment.WebRootPath;
    if (!string.IsNullOrWhiteSpace(staticRoot) && Directory.Exists(staticRoot))
    {
        _ = app.MapFallbackToFile("index.html").AllowAnonymous(); // Public to serve the login and setup shell.
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
        if (app.Environment.IsEnvironment("Testing")
            || string.Equals(app.Configuration["DISABLE_TELEMETRY"], "true", StringComparison.OrdinalIgnoreCase))
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

static void PruneOpenApiMaintenanceNavigationProperties(JsonTypeInfo jsonTypeInfo)
{
    if (jsonTypeInfo.Kind != JsonTypeInfoKind.Object)
    {
        return;
    }

    string[] propertiesToRemove = jsonTypeInfo.Type switch
    {
        Type type when type == typeof(MaintenanceAlert) =>
        [
            "printer",
            "printerMaintenanceSchedule",
            "maintenanceTask",
            "toolhead"
        ],
        Type type when type == typeof(MaintenanceLog) =>
        [
            "printer",
            "printerMaintenanceSchedule",
            "resolvedAlert",
            "maintenanceTask",
            "toolhead"
        ],
        Type type when type == typeof(PrinterMaintenanceSchedule) =>
        [
            "printer",
            "maintenancePlan",
            "toolhead"
        ],
        Type type when type == typeof(PrinterStatistics) =>
        [
            "printer"
        ],
        _ => []
    };

    foreach (string propertyName in propertiesToRemove)
    {
        JsonPropertyInfo? property = jsonTypeInfo.Properties.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        if (property is not null)
        {
            _ = jsonTypeInfo.Properties.Remove(property);
        }
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
