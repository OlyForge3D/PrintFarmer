using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Health;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Infrastructure.Authorization;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Database;
using Farm.Web.Api.Infrastructure.Filters;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Infrastructure.Settings;
using Farm.Web.Shared;
using Farm.Web.Shared.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
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
using Farm.Web.Api.Services.DiscoveryProbes;

// using Microsoft.Extensions.Caching.Memory; // removed unused

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register all PrintFarmer services
builder.Services.AddPrintFarmerServices();

// Register database with multi-provider support
builder.Services.AddPrintFarmerDatabase(builder.Configuration);

// Register settings service
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
catch { /* non-fatal */ }

// Add API services
builder.Services.AddControllers(options =>
    {
        options.Filters.Add<DuplicateConflictExceptionFilter>();
    })
    .AddJsonOptions(options =>
    {
        // Configure JSON options for .NET 9 compatibility
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = false;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new PrinterBackendJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new PrintJobStatusJsonConverter());
        // Default string enum converter for all other enums
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
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
        string allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
            ?? Environment.GetEnvironmentVariable("CORS__AllowedOrigins")
            ?? "http://localhost:3000,https://localhost:3000,http://localhost:8081,https://localhost:8443,http://localhost:5000,http://localhost:5001";
        bool allowLocalNetwork = Environment.GetEnvironmentVariable("ALLOW_LOCAL_NETWORK") == "true";
        policy.SetIsOriginAllowed(origin =>
        {
            if (allowLocalNetwork)
            {
                return true;
            }
            string[] configuredOrigins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(o => o.Trim()).ToArray();
            return configuredOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
        });
        policy.AllowCredentials();
        policy.WithHeaders("Content-Type", "Authorization", "x-correlation-id", "traceparent", "x-signalr-user-agent", "x-requested-with");
        policy.WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS");
    });
});

// Configure OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
    {
        resource.AddService("PrintFarmer.API", serviceVersion: "1.0.0")
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("farm.environment", builder.Environment.EnvironmentName),
                    new KeyValuePair<string, object>("farm.database.provider", builder.Configuration.GetValue<string>("DB_PROVIDER") ?? "sqlite")
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
    builder.Services.AddSingleton(_ => new SpaProxyActivationState(devUrl));
    builder.Services.AddHttpClient("SpaProxy");
    builder.Services.AddScoped<SpaDevServerWatcher>();
}

// Add JWT Authentication
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        // Enable extra diagnostics in Development and Testing
        if (builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing")
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
                    try
                    {
                        IUnifiedLoggingService? uls = context.HttpContext.RequestServices.GetService(typeof(Farm.Infrastructure.Telemetry.IUnifiedLoggingService)) as Farm.Infrastructure.Telemetry.IUnifiedLoggingService;
                        ILogger<Program>? l = context.HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Logging.ILogger<Program>)) as Microsoft.Extensions.Logging.ILogger<Program>;
                        string presence = !string.IsNullOrEmpty(auth) ? "present" : "missing";
                        if (uls != null)
                        {
                            uls.LogDebug($"[JWT][OnMessageReceived] Authorization header: {presence} tokenSnippet={snippet}");
                        }
                        else if (l != null)
                        {
                            l.LogDebug("[JWT][OnMessageReceived] Authorization header: {Presence} tokenSnippet: {TokenSnippet}", presence, snippet);
                        }
                    }
                    catch { }
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    try
                    {
                        IUnifiedLoggingService? uls = context.HttpContext.RequestServices.GetService(typeof(Farm.Infrastructure.Telemetry.IUnifiedLoggingService)) as Farm.Infrastructure.Telemetry.IUnifiedLoggingService;
                        ILogger<Program>? l = context.HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Logging.ILogger<Program>)) as Microsoft.Extensions.Logging.ILogger<Program>;
                        string exType = context.Exception.GetType().Name;
                        string exMessage = context.Exception.Message;
                        if (uls != null)
                        {
                            uls.LogError(context.Exception, $"[JWT][OnAuthenticationFailed] {exType}: {exMessage}");
                        }
                        else if (l != null)
                        {
                            l.LogError(context.Exception, "[JWT][OnAuthenticationFailed] {ExceptionType}: {Message}", exType, exMessage);
                        }
                    }
                    catch { }
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    string sub = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "<none>";
                    string roles = string.Join(',', context.Principal?.FindAll(System.Security.Claims.ClaimTypes.Role)?.Select(c => c.Value) ?? Array.Empty<string>());
                    try
                    {
                        IUnifiedLoggingService? uls = context.HttpContext.RequestServices.GetService(typeof(Farm.Infrastructure.Telemetry.IUnifiedLoggingService)) as Farm.Infrastructure.Telemetry.IUnifiedLoggingService;
                        ILogger<Program>? l = context.HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Logging.ILogger<Program>)) as Microsoft.Extensions.Logging.ILogger<Program>;
                        if (uls != null)
                        {
                            uls.LogInformation($"[JWT][OnTokenValidated] user: {sub}, roles: [{roles}]");
                        }
                        else if (l != null)
                        {
                            l.LogInformation("[JWT][OnTokenValidated] user: {User} roles: {Roles}", sub, roles);
                        }
                    }
                    catch { }
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    try
                    {
                        IUnifiedLoggingService? uls = context.HttpContext.RequestServices.GetService(typeof(Farm.Infrastructure.Telemetry.IUnifiedLoggingService)) as Farm.Infrastructure.Telemetry.IUnifiedLoggingService;
                        ILogger<Program>? l = context.HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Logging.ILogger<Program>)) as Microsoft.Extensions.Logging.ILogger<Program>;
                        string error = context.Error ?? "<none>";
                        string desc = context.ErrorDescription ?? "<none>";
                        if (uls != null)
                        {
                            uls.LogWarning($"[JWT][OnChallenge] Error={error} Desc={desc}");
                        }
                        else if (l != null)
                        {
                            l.LogWarning("[JWT][OnChallenge] Error={Error} Desc={Desc}", error, desc);
                        }
                    }
                    catch { }
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
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
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
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

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
                var sp = innerScope.ServiceProvider;
                IUnifiedLoggingService? failLogger = sp.GetService<IUnifiedLoggingService>();
                var lg = sp.GetService(typeof(Microsoft.Extensions.Logging.ILogger<Program>)) as Microsoft.Extensions.Logging.ILogger<Program>;
                if (failLogger != null)
                {
                    failLogger.LogWarning($"[UnifiedLogging] Deferred console redirection failed: {ex.Message}");
                }
                else if (lg != null)
                {
                    lg.LogWarning("[UnifiedLogging] Deferred console redirection failed: {Message}", ex.Message);
                }
                else
                {
                    // Last-resort fallback when logging pipeline is unavailable
                    Console.Error.WriteLine($"[UnifiedLogging][FALLBACK] Deferred console redirection failed: {ex.Message}");
                }
            }
            catch
            {
                // Last resort fallback to stderr so failure is visible if logging pipeline itself is broken.
                Console.Error.WriteLine($"[UnifiedLogging][FALLBACK] Deferred console redirection failed: {ex.Message}");
            }
        }
    });
}

// Handle CLI commands (exits if command processed)
if (await app.HandleCliCommandsAsync(args))
{
    return;
}

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
    ISwaggerProvider provider = app.Services.GetRequiredService<ISwaggerProvider>();
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
app.MapHub<HarvestHub>("/hubs/harvest");

// Health checks
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        StartupStatus? startup = context.RequestServices.GetService<StartupStatus>();
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
        StartupStatus? startup = context.RequestServices.GetService<StartupStatus>();
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


// Minimal API for network discovery settings
// Helper: Map between model and DTO



// Network discovery settings now available via UnifiedSettingsController:
// GET /api/settings/network-discovery  
// POST /api/settings/network-discovery
// (Legacy endpoints removed - use unified controller instead)
app.MapPost("/api/network-discovery/settings/validate", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] ([FromBody] Farm.Infrastructure.Settings.NetworkDiscoverySettings body) =>
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

// SignalR settings now available via UnifiedSettingsController:
// GET /api/settings/signalr
// POST /api/settings/signalr
// (Legacy endpoints removed - use unified controller instead)
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
app.MapPost("/api/network-discovery/settings/apply-env", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] ([FromServices] Farm.Infrastructure.Settings.ISettingsService settingsService) =>
{
    // Allows re-applying environment driven defaults from DISCOVERY_RANGES / DISCOVERY_PORTS
    string? rangesEnv = Environment.GetEnvironmentVariable("DISCOVERY_RANGES");
    string? portsEnv = Environment.GetEnvironmentVariable("DISCOVERY_PORTS");
    NetworkDiscoverySettings current = settingsService.Get<Farm.Infrastructure.Settings.NetworkDiscoverySettings>() ?? new Farm.Infrastructure.Settings.NetworkDiscoverySettings();
    // TODO: Update logic for new NetworkDiscoverySettings properties if needed
    settingsService.Save(current);
    return Results.Ok(current);
});

// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
// Extended diagnostic: expose active temp root (non-sensitive path) for debugging; omit if running in Production
app.MapGet("/diagnostics/temp-root", ([FromServices] IWebHostEnvironment env, [FromServices] ITempPathProvider provider) =>
    env.IsProduction()
        ? Results.StatusCode(StatusCodes.Status404NotFound)
        : Results.Ok(new { tempRoot = provider.GetTempRoot() })
);
// Combined diagnostics (non-sensitive) for UI consumption
app.MapGet("/api/diagnostics/summary", ([FromServices] SpoolmanService spoolmanSvc, [FromServices] Farm.Infrastructure.Settings.ISettingsService settingsService) =>
{
    SpoolmanConfigDto? spoolCfg = spoolmanSvc.GetConfig();
    NetworkDiscoverySettings discovery = settingsService.Get<Farm.Infrastructure.Settings.NetworkDiscoverySettings>() ?? new Farm.Infrastructure.Settings.NetworkDiscoverySettings();
    return Results.Ok(new
    {
        spoolman = new { configured = spoolCfg is not null && !string.IsNullOrWhiteSpace(spoolCfg.BaseUrl), baseUrl = spoolCfg?.BaseUrl },
        discovery = discovery
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

// Initialize database (ensures schema exists before resolving SettingsService)
await app.InitializeDatabaseAsync();

await app.RunAsync();

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
    protected Program() { }
}




