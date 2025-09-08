using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Farm.Web.Api.Configuration;
using Farm.Web.Api.Data;
using Farm.Web.Api.Health;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.SlicerServices;
using StackExchange.Redis;
using Farm.Web.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
builder.Services.AddSwaggerGen(options =>
{
    // Include XML documentation if generated (for enriched Swagger docs)
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
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
                {
                    return true;
                }

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
        {
            return false;
        }

        var ranges = networkRanges.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var range in ranges)
        {
            var parts = range.Trim().Split('/');
            if (parts.Length != 2)
            {
                continue;
            }

            if (System.Net.IPAddress.TryParse(parts[0], out var networkAddress) &&
                int.TryParse(parts[1], out var prefixLength) &&
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
    var ipBytes = ipAddress.GetAddressBytes();
    var networkBytes = networkAddress.GetAddressBytes();

    if (ipBytes.Length != networkBytes.Length)
    {
        return false;
    }

    var bytesToCheck = prefixLength / 8;
    var bitsToCheck = prefixLength % 8;

    for (int i = 0; i < bytesToCheck; i++)
    {
        if (ipBytes[i] != networkBytes[i])
        {
            return false;
        }
    }

    if (bitsToCheck > 0 && bytesToCheck < ipBytes.Length)
    {
        var mask = (byte)(0xFF << (8 - bitsToCheck));
        if ((ipBytes[bytesToCheck] & mask) != (networkBytes[bytesToCheck] & mask))
        {
            return false;
        }
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
builder.Services.AddSingleton<IDiscoveryProgressCache, DiscoveryProgressCache>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<ConfigurationValidator>();
builder.Services.AddScoped<IMoonrakerClient, MoonrakerClient>();
builder.Services.AddScoped<IPrusaLinkClient, PrusaLinkClient>();
builder.Services.AddHttpClient<SdcpClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<ISdcpClient, SdcpClient>();
builder.Services.AddScoped<ICircuitBreakerService, CircuitBreakerService>();
builder.Services.AddScoped<IGcodeHarvestService, GcodeHarvestService>();
builder.Services.AddScoped<GcodeHarvestService>();

// Harvest queue services
builder.Services.AddSingleton<IHarvestQueue, InMemoryHarvestQueue>();

// Slicer services
builder.Services.Configure<MockSlicerOptions>(builder.Configuration.GetSection("MockSlicer"));
builder.Services.Configure<LocalFileStorageOptions>(builder.Configuration.GetSection("LocalFileStorage"));

// Add Redis connection for slicer job queue
builder.Services.AddSingleton<IConnectionMultiplexer>(provider =>
{
    var configuration = provider.GetService<IConfiguration>();
    var connectionString = configuration?.GetConnectionString("Redis") ?? "localhost:6379";
    return ConnectionMultiplexer.Connect(connectionString);
});

builder.Services.AddScoped<ISlicerEngine, MockOrcaSlicerEngine>();
builder.Services.AddScoped<ISlicerEngine, MockPrusaSlicerEngine>();
builder.Services.AddScoped<ISlicerJobQueue, RedisSlicerJobQueue>();
builder.Services.AddScoped<ISlicerFileStorage, LocalSlicerFileStorage>();
builder.Services.AddScoped<ISlicerProgressNotifier, SignalRSlicerProgressNotifier>();
builder.Services.AddScoped<ISlicerOrchestrator, SlicerOrchestrator>();

// Background services
builder.Services.AddHostedService<MoonrakerSubscriptionService>();
builder.Services.AddHostedService<HarvestWorkerService>();
builder.Services.AddHostedService<HarvestCompletionService>();
builder.Services.AddHostedService<GracefulShutdownService>();

// SignalR for real-time updates
builder.Services.AddSignalR();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<ComprehensiveHealthCheck>("comprehensive")
    .AddCheck<SignalRHealthCheck>("signalr");

// Validation
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// SPA services (only for monolithic deployments)
var isMonolithicDeployment = builder.Configuration.GetValue<string>("DEPLOYMENT_MODE") != "microservices";
if (isMonolithicDeployment)
{
    builder.Services.AddSpaStaticFiles(configuration =>
    {
        configuration.RootPath = "wwwroot";
    });
}

// Dynamic SPA dev proxy support (development only)
if (isMonolithicDeployment && builder.Environment.IsDevelopment())
{
    // Default dev server URL (configurable via SPA_DEV_URL); using widely adopted Vite default.
    var devUrl = builder.Configuration.GetValue<string>("SPA_DEV_URL");
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
                    var auth = context.Request.Headers["Authorization"].ToString();
                    string snippet = "";
                    if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        var tok = auth.Substring("Bearer ".Length).Trim();
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
                    var sub = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "<none>";
                    var roles = string.Join(',', context.Principal?.FindAll(System.Security.Claims.ClaimTypes.Role)?.Select(c => c.Value) ?? Array.Empty<string>());
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

        var key = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured");
        var issuer = builder.Configuration["Jwt:Issuer"] ?? "PrintFarmer";
        var audience = builder.Configuration["Jwt:Audience"] ?? "PrintFarmer";

        var tvp = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
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
});

// Register authorization handlers
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Farm.Web.Api.Infrastructure.Authorization.PermissionAuthorizationHandler>();

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

    // Seed authentication data (idempotent)
    await Farm.Web.Api.Data.Seed.AuthenticationDataSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());

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

// Always expose raw OpenAPI JSON at a stable path for tooling (even outside dev UI)
app.MapGet("/openapi.json", (Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider adp) =>
{
    // Delegate to internal swagger generator service
    var provider = app.Services.GetRequiredService<Swashbuckle.AspNetCore.Swagger.ISwaggerProvider>();
    var doc = provider.GetSwagger("v1");
    return Results.Json(doc);
});

app.UseCors("Default");

// Authentication and Authorization
app.UseAuthentication();
app.UseAuthorization();

// Configure API routing and SignalR hubs
app.MapControllers();
app.MapHub<PrinterHub>("/hubs/printers");

// Health checks
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = JsonSerializer.Serialize(
            new
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
        var result = JsonSerializer.Serialize(
            new
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
            },
            Program.HealthJsonOptions);

        await context.Response.WriteAsync(result);
    }
});

// Minimal API for presets
app.MapGet("/api/presets", ([FromServices] IPresetService svc) => Results.Ok(svc.GetPresets()));
app.MapPost("/api/presets", ([FromServices] IPresetService svc, [FromBody] FilamentPresetsDto body) => { svc.SavePresets(body); return Results.NoContent(); });

// Minimal API for network discovery settings
app.MapGet("/api/network-discovery/settings", ([FromServices] INetworkDiscoverySettingsService svc) => Results.Ok(svc.GetSettings()));
app.MapPost("/api/network-discovery/settings", ([FromServices] INetworkDiscoverySettingsService svc, [FromBody] NetworkDiscoverySettingsDto body) => { svc.SaveSettings(body); return Results.NoContent(); });

// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
// Compatibility alias sometimes requested by clients/proxies expecting under /api prefix
app.MapGet("/api/healthz", () => Results.Ok(new { status = "ok" }));

// Configure SPA only for monolithic deployments (not microservices)
if (isMonolithicDeployment)
{
    app.UseStaticFiles();

    if (app.Environment.IsDevelopment())
    {
        // Dynamic proxy middleware will handle forwarding once dev server becomes available
        app.UseMiddleware<SpaDynamicProxyMiddleware>();
    }
    else
    {
        // Production: serve pre-built SPA assets
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
