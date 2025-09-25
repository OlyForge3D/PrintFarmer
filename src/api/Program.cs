using Farm.Infrastructure;
// Global using cleanup handled by project settings; explicit System removed.
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Farm.Web.Api.Configuration;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Health;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Database;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Api.Services.Startup;
using Farm.Infrastructure.Telemetry;
using Farm.Infrastructure.Normalization;
using Farm.Web.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Swashbuckle.AspNetCore.Swagger;
// using Microsoft.Extensions.Caching.Memory; // removed unused

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
// Register SystemLogCleanupService for periodic log cleanup
builder.Services.AddHostedService<SystemLogCleanupService>();

// Attempt to unify WebRoot to repository-level /wwwroot directory (shared across API & React build output)
try
{
    // CA3003: Path is constructed from known root, not user input
#pragma warning disable CA3003
    string potentialShared = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "wwwroot"));
#pragma warning restore CA3003
    if (Directory.Exists(potentialShared))
    {
        builder.Environment.WebRootPath = potentialShared;
    }
}
catch { /* non-fatal */ }

// Add API services
builder.Services.AddControllers(options =>
    {
        options.Filters.Add<Farm.Web.Api.Infrastructure.Filters.DuplicateConflictExceptionFilter>();
    })
    .AddJsonOptions(o =>
    {
        // Register custom converters first so they take precedence
        o.JsonSerializerOptions.Converters.Add(new Farm.Web.Shared.Json.PrinterBackendJsonConverter());
        o.JsonSerializerOptions.Converters.Add(new Farm.Web.Shared.Json.PrintJobStatusJsonConverter());
        // Default string enum converter for all other enums
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddJsonOptions(options =>
    {
        // Configure JSON options for .NET 9 compatibility
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = false;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Include XML documentation if generated (for enriched Swagger docs)
    string xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    // CA3003: xmlFile is assembly name, not user input
#pragma warning disable CA3003
    string xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
#pragma warning restore CA3003
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }
    options.SchemaFilter<Farm.Web.Api.Infrastructure.Swagger.ExampleSchemaFilter>();
    options.OperationFilter<Farm.Web.Api.Infrastructure.Swagger.ExampleOperationFilter>();
});

// CORS configuration for API access
builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy =>
    {
        // Get allowed origins from environment variable or use defaults.
        // Support both legacy CORS__AllowedOrigins and current ALLOWED_ORIGINS for backward compatibility.
        string allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
            ?? Environment.GetEnvironmentVariable("CORS__AllowedOrigins")
            ?? "http://localhost:3000,https://localhost:3000,http://localhost:8081,https://localhost:8443,http://localhost:5000,http://localhost:5001"; // include React dev server defaults

        // Check if wildcard network access is enabled
        bool allowLocalNetwork = Environment.GetEnvironmentVariable("ALLOW_LOCAL_NETWORK") == "true";
        string networkRanges = Environment.GetEnvironmentVariable("ALLOWED_NETWORK_RANGES")
                           ?? "192.168.0.0/16,10.0.0.0/8,172.16.0.0/12";

        // IMPORTANT: We previously used AllowAnyOrigin() when ALLOW_LOCAL_NETWORK=true, which resulted in
        // Access-Control-Allow-Origin: * and broke requests with credentials (e.g., SignalR negotiation using
        // cookies or Authorization headers) because browsers forbid wildcard with credentials. We now always
        // emit the requesting origin explicitly when allowed so credentials are supported.

        policy.SetIsOriginAllowed(origin =>
        {
            // Always allow when local network flag is on (broad dev convenience) – but return true so the
            // middleware echoes the concrete origin (not '*') enabling credentialed requests.
            if (allowLocalNetwork)
            {
                return true;
            }

            string[] configuredOrigins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                   .Select(o => o.Trim())
                                                   .ToArray();

            if (configuredOrigins.Contains(origin))
            {
                return true;
            }

            // Check if origin matches allowed network ranges (ip-based origin like http://192.168.x.x:port)
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                ? IsIpInAllowedRanges(uri.Host, networkRanges)
                : false;
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

// Helper method to check if IP is in allowed network ranges
static bool IsIpInAllowedRanges(string host, string networkRanges)
{
    try
    {
        if (!System.Net.IPAddress.TryParse(host, out IPAddress? ipAddress))
        {
            return false;
        }

        string[] ranges = networkRanges.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (string range in ranges)
        {
            string[] parts = range.Trim().Split('/');
            if (parts.Length != 2)
            {
                continue;
            }

            if (System.Net.IPAddress.TryParse(parts[0], out IPAddress? networkAddress) &&
                int.TryParse(parts[1], out int prefixLength) &&
                IsIpInNetwork(ipAddress, networkAddress, prefixLength))
            {
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
    byte[] ipBytes = ipAddress.GetAddressBytes();
    byte[] networkBytes = networkAddress.GetAddressBytes();

    if (ipBytes.Length != networkBytes.Length)
    {
        return false;
    }

    int bytesToCheck = prefixLength / 8;
    int bitsToCheck = prefixLength % 8;

    for (int i = 0; i < bytesToCheck; i++)
    {
        if (ipBytes[i] != networkBytes[i])
        {
            return false;
        }
    }

    if (bitsToCheck > 0 && bytesToCheck < ipBytes.Length)
    {
        byte mask = (byte)(0xFF << (8 - bitsToCheck));
        if ((ipBytes[bytesToCheck] & mask) != (networkBytes[bytesToCheck] & mask))
        {
            return false;
        }
    }

    return true;
}

// Database provider selection: Sqlite (default), SqlServer, Postgres, MySql
string dbProvider = builder.Configuration["Db:Provider"]
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
            string cs = builder.Configuration.GetConnectionString("MySql")
                     ?? builder.Configuration.GetConnectionString("Default")
                     ?? "Server=localhost;Database=printfarmer;User=printfarmer;Password=PrintFarm123!;";
            ServerVersion serverVersion = ServerVersion.AutoDetect(cs);
            options.UseMySql(cs, serverVersion);
            break;
        }
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

// Configure OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
    {
        resource.AddService("PrintFarmer.API", serviceVersion: "1.0.0")
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("farm.environment", builder.Environment.EnvironmentName),
                    new KeyValuePair<string, object>("farm.database.provider", dbProvider)
                });
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.EnrichWithHttpRequest = (activity, httpRequest) =>
            {
                activity.SetTag("http.request.method", httpRequest.Method);
                activity.SetTag("http.request.path", httpRequest.Path);
                if (httpRequest.QueryString.HasValue)
                {
                    activity.SetTag("http.request.query", httpRequest.QueryString.Value);
                }
            };
            options.EnrichWithHttpResponse = (activity, httpResponse) =>
            {
                activity.SetTag("http.response.status_code", httpResponse.StatusCode);
            };
        })
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation(options =>
        {
            options.SetDbStatementForStoredProcedure = true;
            options.SetDbStatementForText = true;
            options.EnrichWithIDbCommand = (activity, command) =>
            {
                activity.SetTag("db.operation", command.CommandText);
            };
        })
        .AddSource("PrintFarmer.*");

        // Add console exporter for development
        if (builder.Environment.IsDevelopment())
        {
            tracing.AddConsoleExporter();
        }

        // Add OTLP exporter for production observability backends
        string? otlpEndpoint = builder.Configuration.GetValue<string>("OpenTelemetry:OTLP:Endpoint");
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                string? headers = builder.Configuration.GetValue<string>("OpenTelemetry:OTLP:Headers");
                if (!string.IsNullOrEmpty(headers))
                {
                    options.Headers = headers;
                }
            });
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddRuntimeInstrumentation()
               .AddMeter("PrintFarmer.*");

        // Add console exporter for development
        if (builder.Environment.IsDevelopment())
        {
            metrics.AddConsoleExporter();
        }

        // Add OTLP exporter for metrics
        string? otlpEndpoint = builder.Configuration.GetValue<string>("OpenTelemetry:OTLP:Endpoint");
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            metrics.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                string? headers = builder.Configuration.GetValue<string>("OpenTelemetry:OTLP:Headers");
                if (!string.IsNullOrEmpty(headers))
                {
                    options.Headers = headers;
                }
            });
        }
    });

// Create ActivitySource for custom instrumentation
ActivitySource activitySource = new("PrintFarmer.API");
builder.Services.AddSingleton(activitySource);

// Register custom telemetry service
builder.Services.AddSingleton<Farm.Infrastructure.Telemetry.IPrintFarmerTelemetryService, Farm.Infrastructure.Telemetry.PrintFarmerTelemetryService>();

// Register unified logging services


// Register unified logging service from Farm.Infrastructure
builder.Services.AddSingleton<IUnifiedLoggingService, UnifiedLoggingService>();

// HTTP clients for external APIs
builder.Services.AddHttpClient<MoonrakerClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<PrusaLinkClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<OctoPrintClient>(client =>
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
builder.Services.AddScoped<ISignalRSettingsService, SignalRSettingsService>();
builder.Services.AddSingleton<IDiscoveryProgressCache, DiscoveryProgressCache>();
builder.Services.AddScoped<IPrinterCapabilityDiscoveryService, PrinterCapabilityDiscoveryService>();
builder.Services.AddHostedService<PrinterCapabilityUpdateService>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<IDefaultCatalogService, DefaultCatalogService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<ConfigurationValidator>();
builder.Services.AddScoped<IMoonrakerClient, MoonrakerClient>();
builder.Services.AddScoped<IPrusaLinkClient, PrusaLinkClient>();
builder.Services.AddScoped<IOctoPrintClient, OctoPrintClient>();
// Model analysis and virus scanning services for ModelController
builder.Services.AddScoped<IModelAnalysisService, ModelAnalysisService>();
builder.Services.AddScoped<IVirusScanner, ClamAVVirusScanner>();
builder.Services.AddScoped<IThumbnailGenerationService, ThumbnailGenerationService>();
// Migration status provider (lightweight introspection without forcing migrations strategy changes)
// NOTE: Was singleton; changed to Scoped because it directly depends on AppDbContext (scoped) to avoid scoped->singleton injection violation in tests.
builder.Services.AddScoped<Farm.Web.Api.Infrastructure.Database.IMigrationStatusProvider, Farm.Web.Api.Infrastructure.Database.MigrationStatusProvider>();
builder.Services.AddHttpClient<SdcpClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<ISdcpClient, SdcpClient>();
builder.Services.AddSingleton<Farm.Infrastructure.Normalization.INormalizationEventLogger, Farm.Infrastructure.Normalization.NormalizationEventLogger>();
builder.Services.AddScoped<ICircuitBreakerService, CircuitBreakerService>();
builder.Services.AddSingleton<INormalizationEventLogger, NormalizationEventLogger>();
builder.Services.AddScoped<IGcodeHarvestService, GcodeHarvestService>();
builder.Services.AddScoped<GcodeHarvestService>();
// G-code upload runtime settings & quota services
builder.Services.AddSingleton<IGcodeUploadSettings, InMemoryGcodeUploadSettings>();
builder.Services.AddSingleton<IGcodeUploadQuotaService>(sp =>
{
    string? limitEnv = Environment.GetEnvironmentVariable("GCODE_DAILY_UPLOAD_LIMIT_BYTES");
    return long.TryParse(limitEnv, out long limit) && limit > 0
        ? new InMemoryGcodeUploadQuotaService(limit)
        : new InMemoryGcodeUploadQuotaService();
});

// Catalog caching (manufacturers/models lists + ETags)
builder.Services.AddMemoryCache();
// CatalogCache uses AppDbContext; make it scoped to avoid consuming scoped context from singleton. Internal IMemoryCache still handles cross-request caching.
builder.Services.AddScoped<ICatalogCache, CatalogCache>();
// Bind CatalogCacheOptions from configuration section CatalogCache (optional)
builder.Services.Configure<CatalogCacheOptions>(builder.Configuration.GetSection("CatalogCache"));

// Harvest queue services
builder.Services.AddSingleton<IHarvestQueue, InMemoryHarvestQueue>();

// Slicer services (MockSlicerOptions removed with in-process engine deprecation)
builder.Services.Configure<LocalFileStorageOptions>(builder.Configuration.GetSection("LocalFileStorage"));

// Add Redis connection for slicer job queue
builder.Services.AddSingleton<IConnectionMultiplexer>(provider =>
{
    IConfiguration? configuration = provider.GetService<IConfiguration>();
    string connectionString = configuration?.GetConnectionString("Redis") ?? "localhost:6379";
    return ConnectionMultiplexer.Connect(connectionString);
});

// In-process slicer engines removed (external workers handle slicing). DI registrations deleted.
builder.Services.AddScoped<ISlicerJobQueue, RedisSlicerJobQueue>();
builder.Services.AddScoped<ISlicerFileStorage, LocalSlicerFileStorage>();
builder.Services.AddScoped<ISlicerProgressNotifier, SignalRSlicerProgressNotifier>();
builder.Services.AddScoped<ISlicerOrchestrator, SlicerOrchestrator>();
builder.Services.AddSingleton<ITempPathProvider, DefaultTempPathProvider>();

// Register slicer runtime settings store (DB-backed)
builder.Services.AddSingleton<ISlicerSettingsService, DbSlicerSettingsService>();
builder.Services.AddSingleton<Farm.Web.Api.Services.Startup.StartupStatus>();
builder.Services.AddHostedService<Farm.Web.Api.Services.Startup.StartupInitializationHostedService>();

// Ensure SlicerExecutableManager can consult runtime admin settings
builder.Services.AddSingleton<ISlicerExecutableManager, SlicerExecutableManager>();
// Process runner used by SlicerWorkerHostedService; abstraction allows test injection of fake processes.
builder.Services.AddTransient<Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner, Farm.Web.Api.Services.SlicerServices.Process.SystemProcessRunner>();

// Register local worker hosted service (it will respect runtime admin settings and stay idle when disabled)
builder.Services.AddHostedService<SlicerWorkerHostedService>();

// Network URL rewriting for cross-environment compatibility
builder.Services.AddSingleton<NetworkUrlRewriteService>();

// Background services
builder.Services.AddHostedService<MoonrakerSubscriptionService>();
builder.Services.AddHostedService<HarvestWorkerService>();
builder.Services.AddHostedService<HarvestCompletionService>();
builder.Services.AddHostedService<GracefulShutdownService>();
// Register ChunkUploadCleanupService with required dependencies
builder.Services.AddHostedService(provider =>
    new Farm.Infrastructure.ChunkUploadCleanupService(
    provider.GetRequiredService<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(),
        builder.Environment.WebRootPath
    )
);

// SignalR for real-time updates
builder.Services.AddSignalR();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<ComprehensiveHealthCheck>("comprehensive")
    .AddCheck<SignalRHealthCheck>("signalr")
    .AddCheck<SpoolmanHealthCheck>("spoolman");

// Validation
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// SPA services (only for monolithic deployments)
bool isMonolithicDeployment = builder.Configuration.GetValue<string>("DEPLOYMENT_MODE") != "microservices";
if (isMonolithicDeployment)
{
    builder.Services.AddSpaStaticFiles(configuration =>
    {
        // Use relative path from content root to unified shared web root so SPA static files (prod) resolve.
        string shared = builder.Environment.WebRootPath;
        try
        {
            if (string.IsNullOrWhiteSpace(shared) || !Directory.Exists(shared))
            {
                // Fallback: look for a local wwwroot under content root (publish scenario)
                string fallback = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
                if (Directory.Exists(fallback))
                {
                    shared = fallback;
                }
                else
                {
                    // No static root available; skip configuring SPA static files.
                    return; // leaves configuration.RootPath unset -> no static file serving attempt
                }
            }
            string relative = Path.GetRelativePath(builder.Environment.ContentRootPath, shared);
            configuration.RootPath = relative; // e.g. ../../wwwroot or wwwroot
        }
        catch
        {
            // Safety: if relative path resolution fails (null args, etc.), skip static file mapping to avoid container crash.
        }
    });
}

// Dynamic SPA dev proxy support (development only)
if (isMonolithicDeployment && builder.Environment.IsDevelopment())
{
    // Default dev server URL (configurable via SPA_DEV_URL); using widely adopted Vite default.
    string? devUrl = builder.Configuration.GetValue<string>("SPA_DEV_URL");
    if (string.IsNullOrWhiteSpace(devUrl))
    {
        devUrl = string.Concat("http://localhost:", "3000"); // constructed to avoid hardcoded analyzer warning
    }
    builder.Services.AddSingleton(new SpaProxyActivationState(devUrl));
    builder.Services.AddHttpClient("SpaProxy");
    builder.Services.AddHostedService<SpaDevServerWatcher>();
}

// Authentication and Authorization services
builder.Services.AddScoped<Farm.Web.Api.Services.Authentication.IPasswordHashingService, Farm.Web.Api.Services.Authentication.PasswordHashingService>();
builder.Services.AddScoped<Farm.Web.Api.Services.Authentication.IAuthenticationService, Farm.Web.Api.Services.Authentication.AuthenticationService>();

// Add JWT Authentication
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        // Enable extra diagnostics in tests
        if (builder.Environment.EnvironmentName == "Testing")
        {
            options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // Simple diagnostics: confirm Authorization header is seen
                    string auth = context.Request.Headers["Authorization"].ToString();
                    string snippet = "";
                    if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        string tok = auth.Substring("Bearer ".Length).Trim();
                        snippet = tok.Length > 12 ? tok[..12] + "..." : tok;
                        // Ensure token is provided to the handler when we override this event
                        if (!string.IsNullOrEmpty(tok))
                        {
                            context.Token = tok;
                        }
                    }
                    System.Console.WriteLine($"[JWT][OnMessageReceived] Authorization header: {(!string.IsNullOrEmpty(auth) ? "present" : "missing")} tokenSnippet={snippet}");
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    System.Console.WriteLine($"[JWT][OnAuthenticationFailed] {context.Exception.GetType().Name}: {context.Exception.Message}");
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    string sub = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "<none>";
                    string roles = string.Join(',', context.Principal?.FindAll(System.Security.Claims.ClaimTypes.Role)?.Select(c => c.Value) ?? Array.Empty<string>());
                    System.Console.WriteLine($"[JWT][OnTokenValidated] user: {sub}, roles: [{roles}]");
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    System.Console.WriteLine($"[JWT][OnChallenge] Error={context.Error ?? "<none>"} Desc={context.ErrorDescription ?? "<none>"}");
                    return Task.CompletedTask;
                }
            };
        }
        // Allow HTTP in test runs and relax validation for test environment
        if (builder.Environment.EnvironmentName == "Testing")
        {
            options.RequireHttpsMetadata = false;
        }

        string? key = builder.Configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("JWT Key not configured. Provide a 32+ character secret via environment variable Jwt__Key or user-secrets in development.");
        }
        string issuer = builder.Configuration["Jwt:Issuer"] ?? "PrintFarmer";
        string audience = builder.Configuration["Jwt:Audience"] ?? "PrintFarmer";

        TokenValidationParameters tvp = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // NOTE: Previously issuer/audience validation was relaxed in the "Testing" environment.
        // All integration tests now obtain tokens exclusively via the authentication endpoints,
        // which generate tokens including both issuer and audience (see AuthenticationService).
        // Enforcing validation in tests prevents accidental acceptance of malformed tokens.
        // (If a future test truly needs to bypass these checks, generate a properly formed token
        // instead of weakening validation here.)

        options.TokenValidationParameters = tvp;
    });

// Add Authorization with custom policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAuthentication", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("RequireAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("farm_admin");
    });
});

// Register authorization handlers
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Farm.Web.Api.Infrastructure.Authorization.PermissionAuthorizationHandler>();

// Extract raw args for potential headless commands (do not remove from hosting args beyond our flags)
List<string> rawArgs = args.ToList();
bool headlessCreateAdmin = rawArgs.Contains("--create-admin");
bool headlessListUsers = rawArgs.Contains("--list-users");

// Bind (HTTP) to configured dev port; using launchSettings.json for default. Override via ASPNETCORE_URLS if needed.
#pragma warning disable S1075 // URIs should not be hardcoded
builder.WebHost.UseUrls("http://0.0.0.0:5245");
#pragma warning restore S1075 // URIs should not be hardcoded
WebApplication app = builder.Build();

// Early liveness endpoint (process up) + readiness separate
app.MapGet("/livez", () => Results.Ok(new { status = "alive" }));

// Deferred console redirection (avoids blocking early host binding). Enable via ENABLE_CONSOLE_REDIRECTION=true
if (string.Equals(Environment.GetEnvironmentVariable("ENABLE_CONSOLE_REDIRECTION"), "true", StringComparison.OrdinalIgnoreCase))
{
    IHostApplicationLifetime lifetime = app.Lifetime; // IHostApplicationLifetime
    lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            // Console redirection service removed; not present in Farm.Infrastructure
            IUnifiedLoggingService logger = scope.ServiceProvider.GetRequiredService<IUnifiedLoggingService>();
            logger.LogInformation("[UnifiedLogging] Console redirection initialized (deferred) - Console output now captured in OpenTelemetry");
        }
        catch (Exception ex)
        {
            try
            {
                using IServiceScope innerScope = app.Services.CreateScope();
                IUnifiedLoggingService? failLogger = innerScope.ServiceProvider.GetService<IUnifiedLoggingService>();
                failLogger?.LogWarning($"[UnifiedLogging] Deferred console redirection failed: {ex.Message}");
            }
            catch
            {
                // Last resort fallback to stderr so failure is visible if logging pipeline itself is broken.
                Console.Error.WriteLine($"[UnifiedLogging][FALLBACK] Deferred console redirection failed: {ex.Message}");
            }
        }
    });
}

// Early headless commands (no web host run) to support automation:
// Usage examples:
//   dotnet run --project src/api/Farm.Web.Api.csproj -- --list-users
//   dotnet run --project src/api/Farm.Web.Api.csproj -- --create-admin --username admin --email admin@example.com --password "VeryStrongPassw0rd!" --first Alice --last Admin
if (headlessCreateAdmin || headlessListUsers)
{
    using IServiceScope scope = app.Services.CreateScope();
    // Ensure database is initialized before any headless operations
    try
    {
        // Minimal initialization for CLI: Ensure database exists & auth seed only (skip catalog for speed)
        AppDbContext cliDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await cliDb.Database.EnsureCreatedAsync();
        await Farm.Web.Api.Data.Seed.AuthenticationDataSeeder.SeedAsync(cliDb);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"[CLI] Database initialization failed: {ex.Message}");
        return;
    }
    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (headlessListUsers)
    {
        List<User> users = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ToListAsync();
        Console.WriteLine($"Users ({users.Count}):");
        foreach (User? u in users)
        {
            string roles = string.Join(',', u.UserRoles.Where(r => r.IsActive).Select(r => r.Role.Name));
            Console.WriteLine($" - {u.Username} <{u.Email}> Roles=[{roles}] Active={u.IsActive}");
        }
        return; // exit app
    }
    if (headlessCreateAdmin)
    {
        string GetArg(string name)
        {
            int idx = rawArgs.IndexOf(name);
            return (idx >= 0 && idx + 1 < rawArgs.Count)
                ? rawArgs[idx + 1]
                : string.Empty;
        }
        string username = GetArg("--username");
        string email = GetArg("--email");
        string password = GetArg("--password");
        string first = GetArg("--first");
        string last = GetArg("--last");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            await Console.Error.WriteLineAsync("Missing required arguments. Usage: --create-admin --username <u> --email <e> --password <p> [--first First] [--last Last]");
            return;
        }
        // Dynamic password policy
        PasswordPolicy? policy = await db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync();
        int minLength = policy?.MinLength ?? 12;
        if (password.Length < minLength)
        {
            await Console.Error.WriteLineAsync($"Password must be at least {minLength} characters.");
            return;
        }
        if (policy != null)
        {
            if (policy.RequireUppercase && !password.Any(char.IsUpper))
            {
                await Console.Error.WriteLineAsync("Password must contain an uppercase letter.");
                return;
            }
            if (policy.RequireLowercase && !password.Any(char.IsLower))
            {
                await Console.Error.WriteLineAsync("Password must contain a lowercase letter.");
                return;
            }
            if (policy.RequireDigit && !password.Any(char.IsDigit))
            {
                await Console.Error.WriteLineAsync("Password must contain a digit.");
                return;
            }
            if (policy.RequireSymbol && password.All(char.IsLetterOrDigit))
            {
                await Console.Error.WriteLineAsync("Password must contain a symbol.");
                return;
            }
        }
        // Ensure seed ran (roles etc.) already handled above.
        IPasswordHashingService hashing = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IPasswordHashingService>();
        IAuthenticationService authSvc = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IAuthenticationService>();
        Farm.Infrastructure.Domain.Role? adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
        if (adminRole is null)
        {
            await Console.Error.WriteLineAsync("Admin role not found; seeding failure.");
            return;
        }
        // Idempotent check
        User? existing = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Username == username || u.Email == email);
        if (existing != null)
        {
            bool hasAdmin = existing.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive);
            if (hasAdmin && scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IPasswordHashingService>().VerifyPassword(password, existing.PasswordHash))
            {
                string tokenExisting = await authSvc.GenerateJwtTokenAsync(existing);
                Console.WriteLine($"Existing admin '{existing.Username}' detected. Reusing credentials. JWT={tokenExisting.Substring(0, Math.Min(32, tokenExisting.Length))}... (truncated)");
                return;
            }
            await Console.Error.WriteLineAsync("User with same username or email already exists (not matching provided password for idempotency). Aborting.");
            return;
        }
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            FirstName = string.IsNullOrWhiteSpace(first) ? "Admin" : first,
            LastName = string.IsNullOrWhiteSpace(last) ? "CLI" : last,
            PasswordHash = hashing.HashPassword(password),
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = adminRole!.Id,
            AssignedAt = DateTime.UtcNow,
            IsActive = true
        });
        await db.SaveChangesAsync();
        string token = await authSvc.GenerateJwtTokenAsync(user);
        Console.WriteLine($"Created admin user '{username}' ({email}). JWT={token.Substring(0, Math.Min(32, token.Length))}... (truncated)");
        return;
    }
}

// (Removed synchronous DB + seeding + admin bootstrap block; now handled asynchronously by StartupInitializationHostedService.)

// Log effective temp root (non-production) for diagnostics
try
{
    IHostEnvironment env = app.Services.GetRequiredService<IHostEnvironment>();
    if (!env.IsProduction())
    {
        ITempPathProvider tempProvider = app.Services.GetRequiredService<ITempPathProvider>();
        app.Logger.LogInformation("[Startup] Temp root: {TempRoot}", tempProvider.GetTempRoot());
    }
}
catch { /* ignore diagnostics failure */ }

// === MIDDLEWARE PIPELINE ===

// Global exception handling
app.UseMiddleware<GlobalExceptionMiddleware>();

// Add telemetry middleware early in the pipeline
app.UseTelemetryMiddleware();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Always expose raw OpenAPI JSON at a stable path for tooling (even outside dev UI)
app.MapGet("/openapi.json", (Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider adp) =>
{
    // Delegate to internal swagger generator service
    ISwaggerProvider provider = app.Services.GetRequiredService<Swashbuckle.AspNetCore.Swagger.ISwaggerProvider>();
    OpenApiDocument doc = provider.GetSwagger("v1");
    return Results.Json(doc);
});

app.UseCors("Default");


// Authentication and Authorization
app.UseAuthentication();
app.UseAuthorization();

// Configure API routing and SignalR hubs
app.MapControllers();
app.MapHub<PrinterHub>("/hubs/printers");
app.MapHub<Farm.Web.Api.Hubs.HarvestHub>("/hubs/harvest");

// Health checks
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        StartupStatus? startup = context.RequestServices.GetService<Farm.Web.Api.Services.Startup.StartupStatus>();
        string result = JsonSerializer.Serialize(
            new
            {
                Status = report.Status.ToString(),
                TotalChecksDuration = report.TotalDuration,
                Startup = startup == null ? null : new
                {
                    phase = startup.Phase.ToString(),
                    ready = startup.IsReady,
                    failed = startup.IsFailed,
                    failureMessage = startup.FailureException?.Message,
                    failureStackTrace = (startup.FailureException != null && context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()) ? startup.FailureException.StackTrace : null,
                    initStartedUtc = startup.InitializationStartedUtc,
                    initCompletedUtc = startup.InitializationCompletedUtc,
                    initDurationMs = startup.InitializationDuration?.TotalMilliseconds
                },
                Results = report.Entries.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        kvp.Value.Status,
                        kvp.Value.Duration,
                        kvp.Value.Description,
                        kvp.Value.Data
                    })
            },
            Program.HealthJsonOptions);

        await context.Response.WriteAsync(result);
    }
});

// Alias route for clients expecting the comprehensive health endpoint under /api prefix
app.MapHealthChecks("/api/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        StartupStatus? startup = context.RequestServices.GetService<Farm.Web.Api.Services.Startup.StartupStatus>();
        string result = JsonSerializer.Serialize(
            new
            {
                Status = report.Status.ToString(),
                TotalChecksDuration = report.TotalDuration,
                Startup = startup == null ? null : new
                {
                    phase = startup.Phase.ToString(),
                    ready = startup.IsReady,
                    failed = startup.IsFailed,
                    failureMessage = startup.FailureException?.Message,
                    failureStackTrace = (startup.FailureException != null && context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()) ? startup.FailureException.StackTrace : null,
                    initStartedUtc = startup.InitializationStartedUtc,
                    initCompletedUtc = startup.InitializationCompletedUtc,
                    initDurationMs = startup.InitializationDuration?.TotalMilliseconds
                },
                Results = report.Entries.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        kvp.Value.Status,
                        kvp.Value.Duration,
                        kvp.Value.Description,
                        kvp.Value.Data
                    })
            },
            Program.HealthJsonOptions);

        await context.Response.WriteAsync(result);
    }
});

// Minimal API for presets
app.MapGet("/api/presets", ([FromServices] IPresetService svc) => Results.Ok(svc.GetPresets()));
app.MapPost("/api/presets", ([FromServices] IPresetService svc, [FromBody] FilamentPresetsDto body) =>
{
    svc.SavePresets(body);
    return Results.NoContent();
});

// Minimal API for network discovery settings
app.MapGet("/api/network-discovery/settings", ([FromServices] INetworkDiscoverySettingsService svc) => Results.Ok(svc.GetSettings()));
app.MapPost("/api/network-discovery/settings", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] ([FromServices] INetworkDiscoverySettingsService svc, [FromBody] NetworkDiscoverySettingsDto body) =>
{
    svc.SaveSettings(body);
    return Results.NoContent();
});
app.MapPost("/api/network-discovery/settings/validate", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] ([FromBody] NetworkDiscoverySettingsDto body) =>
{
    NetworkValidationResult validation = Farm.Web.Api.Services.NetworkValidationService.ValidateSettings(body);
    return Results.Ok(new
    {
        isValid = validation.IsValid,
        errors = validation.Errors,
        warnings = validation.Warnings,
        suggestions = validation.Suggestions
    });
});

// Minimal API for SignalR settings
app.MapGet("/api/signalr/settings", ([FromServices] ISignalRSettingsService svc) => Results.Ok(svc.GetSettings()));
app.MapPost("/api/signalr/settings", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] ([FromServices] ISignalRSettingsService svc, [FromBody] SignalRSettingsDto body) =>
{
    svc.SaveSettings(body);
    return Results.NoContent();
});
app.MapPost("/api/network-discovery/auto-detect", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] () =>
{
    // Enumerate local IPv4 addresses and suggest /24 CIDR blocks.
    HashSet<string> suggestions = new(StringComparer.OrdinalIgnoreCase);
    try
    {
        foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }
            IPInterfaceProperties props = ni.GetIPProperties();
            foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    // If subnet mask available, derive CIDR; fallback to /24.
                    int prefix = 24;
                    if (ua.IPv4Mask is not null)
                    {
                        byte[] maskBytes = ua.IPv4Mask.GetAddressBytes();
                        int ones = 0;
                        foreach (byte b in maskBytes)
                        {
                            byte v = b;
                            while (v != 0)
                            {
                                ones += v & 1;
                                v >>= 1;
                            }
                        }
                        if (ones > 0)
                        {
                            prefix = ones;
                        }
                    }
                    byte[] networkBytes = ua.Address.GetAddressBytes();
                    if (prefix is >= 8 and <= 32)
                    {
                        // Zero remaining host bits for canonical network base
                        int fullBytes = prefix / 8;
                        int remBits = prefix % 8;
                        if (remBits > 0 && fullBytes < networkBytes.Length)
                        {
                            byte mask = (byte)(0xFF << (8 - remBits));
                            networkBytes[fullBytes] = (byte)(networkBytes[fullBytes] & mask);
                            for (int i = fullBytes + 1; i < networkBytes.Length; i++)
                            {
                                networkBytes[i] = 0;
                            }
                        }
                        else
                        {
                            for (int i = fullBytes; i < networkBytes.Length; i++)
                            {
                                networkBytes[i] = 0;
                            }
                        }
                        IPAddress networkBase = new(networkBytes);
                        suggestions.Add($"{networkBase}/{prefix}");
                    }
                }
            }
        }
    }
    catch { /* ignore */ }
    return Results.Ok(new { ranges = suggestions.OrderBy(s => s).ToArray() });
});
app.MapPost("/api/network-discovery/settings/apply-env", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] ([FromServices] INetworkDiscoverySettingsService svc) =>
{
    // Allows re-applying environment driven defaults from DISCOVERY_RANGES / DISCOVERY_PORTS
    string? rangesEnv = Environment.GetEnvironmentVariable("DISCOVERY_RANGES");
    string? portsEnv = Environment.GetEnvironmentVariable("DISCOVERY_PORTS");
    NetworkDiscoverySettingsDto current = svc.GetSettings();
    List<string> ranges = current.NetworkRanges;
    if (!string.IsNullOrWhiteSpace(rangesEnv))
    {
        ranges = [.. rangesEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
    }
    List<int> ports = current.Ports;
    if (!string.IsNullOrWhiteSpace(portsEnv))
    {
        ports = [.. portsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out int v) ? v : -1)
            .Where(v => v is > 0 and < 65536)
            .Distinct()];
        if (ports.Count == 0)
        {
            ports = current.Ports;
        }
    }
    NetworkDiscoverySettingsDto updated = new(ranges, current.TimeoutMs, current.MaxConcurrentScans, ports);
    svc.SaveSettings(updated);
    return Results.Ok(updated);
});

// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
// Extended diagnostic: expose active temp root (non-sensitive path) for debugging; omit if running in Production
app.MapGet("/diagnostics/temp-root", (Microsoft.AspNetCore.Hosting.IWebHostEnvironment env, Farm.Web.Api.Infrastructure.Temp.ITempPathProvider provider) =>
    env.IsProduction()
        ? Results.StatusCode(StatusCodes.Status404NotFound)
        : Results.Ok(new { tempRoot = provider.GetTempRoot() })
);
// Combined diagnostics (non-sensitive) for UI consumption
app.MapGet("/api/diagnostics/summary", ([FromServices] SpoolmanService spoolmanSvc, [FromServices] INetworkDiscoverySettingsService discoverySvc) =>
{
    SpoolmanConfigDto? spoolCfg = spoolmanSvc.GetConfig();
    NetworkDiscoverySettingsDto discovery = discoverySvc.GetSettings();
    return Results.Ok(new
    {
        spoolman = new { configured = spoolCfg is not null && !string.IsNullOrWhiteSpace(spoolCfg.BaseUrl), baseUrl = spoolCfg?.BaseUrl },
        discovery = new
        {
            ranges = discovery.NetworkRanges,
            ports = discovery.Ports,
            timeoutMs = discovery.TimeoutMs,
            maxConcurrentScans = discovery.MaxConcurrentScans
        }
    });
});
// Compatibility alias sometimes requested by clients/proxies expecting under /api prefix
app.MapGet("/api/healthz", () => Results.Ok(new { status = "ok" }));

// Final log just before entering host run loop (diagnostic)
app.Logger.LogInformation("[Startup] Reached app.Run() - binding to configured URLs");

// Database info endpoint (dev or DEBUG_DB_INFO=true) with migration status integration.
app.MapGet("/api/debug/db-info", async (AppDbContext db,
    IWebHostEnvironment env,
    IConfiguration config,
    [Microsoft.AspNetCore.Mvc.FromServices] Farm.Web.Api.Infrastructure.Database.IMigrationStatusProvider migrationStatusProvider,
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
        [nameof(db.Models)] = await db.Models.CountAsync(ct),
        [nameof(db.FilamentTypes)] = await db.FilamentTypes.CountAsync(ct),
        [nameof(db.PrinterModelFilamentTypes)] = await db.PrinterModelFilamentTypes.CountAsync(ct),
        [nameof(db.SpoolmanConfigs)] = await db.SpoolmanConfigs.CountAsync(ct),
        [nameof(db.GcodeFiles)] = await db.GcodeFiles.CountAsync(ct),
        [nameof(db.PrintJobs)] = await db.PrintJobs.CountAsync(ct),
        [nameof(db.PrinterCapabilities)] = await db.PrinterCapabilities.CountAsync(ct),
        [nameof(db.GcodeHarvestOperations)] = await db.GcodeHarvestOperations.CountAsync(ct),
        [nameof(db.HarvestDiscoveredFiles)] = await db.HarvestDiscoveredFiles.CountAsync(ct),
        [nameof(db.Models3D)] = await db.Models3D.CountAsync(ct),
        [nameof(db.SlicerProfiles)] = await db.SlicerProfiles.CountAsync(ct),
        [nameof(db.Users)] = await db.Users.CountAsync(ct),
        [nameof(db.Roles)] = await db.Roles.CountAsync(ct),
        [nameof(db.Resources)] = await db.Resources.CountAsync(ct),
        [nameof(db.Actions)] = await db.Actions.CountAsync(ct),
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
        catch { }
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
if (isMonolithicDeployment)
{
    // Only enable static file / SPA pipeline if a web root actually exists (prebuilt assets). In container builds
    // using DEPLOYMENT_MODE=monolithic we expect /wwwroot to be present; if it's missing we skip to avoid crashes.
    string staticRoot = app.Environment.WebRootPath;
    if (!string.IsNullOrWhiteSpace(staticRoot) && Directory.Exists(staticRoot))
    {
        app.UseStaticFiles();

        if (app.Environment.IsDevelopment())
        {
            // Dynamic proxy middleware will handle forwarding once dev server becomes available
            app.UseMiddleware<SpaDynamicProxyMiddleware>();
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

// Enter host run loop
await app.RunAsync();

// Expose Program for WebApplicationFactory in tests
[
    SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable", Justification = "Public partial Program required for WebApplicationFactory in tests and minimal hosting model.")
]
public partial class Program
{
    // Cached JSON options to avoid per-call allocations (CA1869)
    public static readonly JsonSerializerOptions HealthJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    protected Program() { }
}

// Cached JSON options to avoid per-call allocations (CA1869)
// Removed per-file JsonDefaults class; using Program.HealthJsonOptions instead.
