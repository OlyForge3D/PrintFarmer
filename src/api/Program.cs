// Global using cleanup handled by project settings; explicit System removed.
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Farm.Web.Api.Configuration;
using Farm.Web.Api.Data;
using Farm.Web.Api.Health;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
// using Microsoft.Extensions.Caching.Memory; // removed unused

var builder = WebApplication.CreateBuilder(args);

// Attempt to unify WebRoot to repository-level /wwwroot directory (shared across API & React build output)
try
{
    var potentialShared = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "wwwroot"));
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
        // Keep default string enum converter for most enums
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        // Add permissive converters for enums that have been causing deserialization failures in tests
        o.JsonSerializerOptions.Converters.Add(new Farm.Web.Shared.Json.PrinterBackendJsonConverter());
        o.JsonSerializerOptions.Converters.Add(new Farm.Web.Shared.Json.PrintJobStatusDtoJsonConverter());
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
        // Get allowed origins from environment variable or use defaults.
        // Support both legacy CORS__AllowedOrigins and current ALLOWED_ORIGINS for backward compatibility.
        var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
            ?? Environment.GetEnvironmentVariable("CORS__AllowedOrigins")
            ?? "http://localhost:3000,https://localhost:3000,http://localhost:8081,https://localhost:8443,http://localhost:5000,http://localhost:5001"; // include React dev server defaults

        // Check if wildcard network access is enabled
        var allowLocalNetwork = Environment.GetEnvironmentVariable("ALLOW_LOCAL_NETWORK") == "true";
        var networkRanges = Environment.GetEnvironmentVariable("ALLOWED_NETWORK_RANGES")
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

            var configuredOrigins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                   .Select(o => o.Trim())
                                                   .ToArray();

            if (configuredOrigins.Contains(origin))
            {
                return true;
            }

            // Check if origin matches allowed network ranges (ip-based origin like http://192.168.x.x:port)
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return IsIpInAllowedRanges(uri.Host, networkRanges);
            }
            return false;
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
// Migration status provider (lightweight introspection without forcing migrations strategy changes)
// NOTE: Was singleton; changed to Scoped because it directly depends on AppDbContext (scoped) to avoid scoped->singleton injection violation in tests.
builder.Services.AddScoped<Farm.Web.Api.Infrastructure.Database.IMigrationStatusProvider, Farm.Web.Api.Infrastructure.Database.MigrationStatusProvider>();
builder.Services.AddHttpClient<SdcpClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<ISdcpClient, SdcpClient>();
builder.Services.AddScoped<ICircuitBreakerService, CircuitBreakerService>();
builder.Services.AddSingleton<INormalizationEventLogger, NormalizationEventLogger>();
builder.Services.AddScoped<IGcodeHarvestService, GcodeHarvestService>();
builder.Services.AddScoped<GcodeHarvestService>();
// G-code upload runtime settings & quota services
builder.Services.AddSingleton<IGcodeUploadSettings, InMemoryGcodeUploadSettings>();
builder.Services.AddSingleton<IGcodeUploadQuotaService>(sp =>
{
    var limitEnv = Environment.GetEnvironmentVariable("GCODE_DAILY_UPLOAD_LIMIT_BYTES");
    if (long.TryParse(limitEnv, out var limit) && limit > 0)
    {
        return new InMemoryGcodeUploadQuotaService(limit);
    }
    return new InMemoryGcodeUploadQuotaService();
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
    var configuration = provider.GetService<IConfiguration>();
    var connectionString = configuration?.GetConnectionString("Redis") ?? "localhost:6379";
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

// Ensure SlicerExecutableManager can consult runtime admin settings
builder.Services.AddSingleton<ISlicerExecutableManager, SlicerExecutableManager>();
// Process runner used by SlicerWorkerHostedService; abstraction allows test injection of fake processes.
builder.Services.AddTransient<Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner, Farm.Web.Api.Services.SlicerServices.Process.SystemProcessRunner>();

// Register local worker hosted service (it will respect runtime admin settings and stay idle when disabled)
builder.Services.AddHostedService<SlicerWorkerHostedService>();

// Background services
builder.Services.AddHostedService<MoonrakerSubscriptionService>();
builder.Services.AddHostedService<HarvestWorkerService>();
builder.Services.AddHostedService<HarvestCompletionService>();
builder.Services.AddHostedService<GracefulShutdownService>();
builder.Services.AddHostedService<Farm.Web.Api.Infrastructure.ChunkUploadCleanupService>();

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
var isMonolithicDeployment = builder.Configuration.GetValue<string>("DEPLOYMENT_MODE") != "microservices";
if (isMonolithicDeployment)
{
    builder.Services.AddSpaStaticFiles(configuration =>
    {
        // Use relative path from content root to unified shared web root so SPA static files (prod) resolve.
        var shared = builder.Environment.WebRootPath;
        try
        {
            if (string.IsNullOrWhiteSpace(shared) || !Directory.Exists(shared))
            {
                // Fallback: look for a local wwwroot under content root (publish scenario)
                var fallback = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
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
            var relative = Path.GetRelativePath(builder.Environment.ContentRootPath, shared);
            configuration.RootPath = relative; // e.g. ../../wwwroot or wwwroot
        }
        catch
        {
            // Safety: if relative path resolution fails (null args, etc.), skip static file mapping to avoid container crash.
            // no-op; fall through
        }
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

        var key = builder.Configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("JWT Key not configured. Provide a 32+ character secret via environment variable Jwt__Key or user-secrets in development.");
        }
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
    options.AddPolicy("RequireAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("farm_admin");
    });
});

// Register authorization handlers
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Farm.Web.Api.Infrastructure.Authorization.PermissionAuthorizationHandler>();

// Extract raw args for potential headless commands (do not remove from hosting args beyond our flags)
var rawArgs = args.ToList();
var headlessCreateAdmin = rawArgs.Contains("--create-admin");
var headlessListUsers = rawArgs.Contains("--list-users");

var app = builder.Build();

// Early headless commands (no web host run) to support automation:
// Usage examples:
//   dotnet run --project src/api/Farm.Web.Api.csproj -- --list-users
//   dotnet run --project src/api/Farm.Web.Api.csproj -- --create-admin --username admin --email admin@example.com --password "VeryStrongPassw0rd!" --first Alice --last Admin
if (headlessCreateAdmin || headlessListUsers)
{
    using var scope = app.Services.CreateScope();
    // Ensure database is initialized before any headless operations
    try
    {
        // Minimal initialization for CLI: Ensure database exists & auth seed only (skip catalog for speed)
        var cliDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await cliDb.Database.EnsureCreatedAsync();
        await Farm.Web.Api.Data.Seed.AuthenticationDataSeeder.SeedAsync(cliDb);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"[CLI] Database initialization failed: {ex.Message}");
        return;
    }
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (headlessListUsers)
    {
        var users = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ToListAsync();
        Console.WriteLine($"Users ({users.Count}):");
        foreach (var u in users)
        {
            var roles = string.Join(',', u.UserRoles.Where(r => r.IsActive).Select(r => r.Role.Name));
            Console.WriteLine($" - {u.Username} <{u.Email}> Roles=[{roles}] Active={u.IsActive}");
        }
        return; // exit app
    }
    if (headlessCreateAdmin)
    {
        string GetArg(string name)
        {
            var idx = rawArgs.IndexOf(name);
            if (idx >= 0 && idx + 1 < rawArgs.Count)
            {
                return rawArgs[idx + 1];
            }
            return string.Empty;
        }
        var username = GetArg("--username");
        var email = GetArg("--email");
        var password = GetArg("--password");
        var first = GetArg("--first");
        var last = GetArg("--last");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            await Console.Error.WriteLineAsync("Missing required arguments. Usage: --create-admin --username <u> --email <e> --password <p> [--first First] [--last Last]");
            return;
        }
        // Dynamic password policy
        var policy = await db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync();
        var minLength = policy?.MinLength ?? 12;
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
        var hashing = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IPasswordHashingService>();
        var authSvc = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IAuthenticationService>();
        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
        if (adminRole == null)
        {
            await Console.Error.WriteLineAsync("Admin role not found; seeding failure.");
            return;
        }
        // Idempotent check
        var existing = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Username == username || u.Email == email);
        if (existing != null)
        {
            var hasAdmin = existing.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive);
            if (hasAdmin && scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IPasswordHashingService>().VerifyPassword(password, existing.PasswordHash))
            {
                var tokenExisting = await authSvc.GenerateJwtTokenAsync(existing);
                Console.WriteLine($"Existing admin '{existing.Username}' detected. Reusing credentials. JWT={tokenExisting.Substring(0, Math.Min(32, tokenExisting.Length))}... (truncated)");
                return;
            }
            await Console.Error.WriteLineAsync("User with same username or email already exists (not matching provided password for idempotency). Aborting.");
            return;
        }
        var user = new Farm.Web.Api.Domain.User
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
        db.UserRoles.Add(new Farm.Web.Api.Domain.UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = adminRole.Id,
            AssignedAt = DateTime.UtcNow,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var token = await authSvc.GenerateJwtTokenAsync(user);
        Console.WriteLine($"Created admin user '{username}' ({email}). JWT={token.Substring(0, Math.Min(32, token.Length))}... (truncated)");
        return;
    }
}

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

    // Optional: Seed Spoolman configuration from environment (one-time if user provided during deploy script)
    try
    {
        var spoolmanBase = Environment.GetEnvironmentVariable("SPOOLMAN_BASE_URL");
        var spoolmanEnabled = Environment.GetEnvironmentVariable("SPOOLMAN_ENABLED");
        if (!string.IsNullOrWhiteSpace(spoolmanBase) && string.Equals(spoolmanEnabled, "yes", StringComparison.OrdinalIgnoreCase))
        {
            var spoolmanSvc = scope.ServiceProvider.GetRequiredService<SpoolmanService>();
            var existing = spoolmanSvc.GetConfig();
            if (existing is null || string.IsNullOrWhiteSpace(existing.BaseUrl))
            {
                spoolmanSvc.SetConfig(new Farm.Web.Shared.SpoolmanConfigDto(spoolmanBase));
                logger.LogInformation("[Startup] Seeded Spoolman configuration from SPOOLMAN_BASE_URL env var: {Url}", spoolmanBase);
            }
            else
            {
                logger.LogDebug("[Startup] Spoolman configuration already present; skipping env seed");
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed Spoolman configuration from environment");
    }

    // Optional unattended initial admin bootstrap (deprecated default). Now requires explicit ENABLE_ADMIN_BOOTSTRAP=true.
    try
    {
        var enableBootstrap = Environment.GetEnvironmentVariable("ENABLE_ADMIN_BOOTSTRAP");
        if (string.Equals(enableBootstrap, "true", StringComparison.OrdinalIgnoreCase))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasAdmin = await db.Users.AnyAsync(u => u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive));
            if (!hasAdmin)
            {
                var adminUser = Environment.GetEnvironmentVariable("ADMIN_USERNAME");
                var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL");
                var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
                if (!string.IsNullOrWhiteSpace(adminUser) && !string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword) && adminPassword.Length >= 12)
                {
                    var hashing = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IPasswordHashingService>();
                    var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
                    if (adminRole != null)
                    {
                        var user = new Farm.Web.Api.Domain.User
                        {
                            Id = Guid.NewGuid(),
                            Username = adminUser,
                            Email = adminEmail,
                            FirstName = "Admin",
                            LastName = "Bootstrap",
                            PasswordHash = hashing.HashPassword(adminPassword),
                            IsActive = true,
                            EmailConfirmed = true,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        db.Users.Add(user);
                        db.UserRoles.Add(new Farm.Web.Api.Domain.UserRole
                        {
                            Id = Guid.NewGuid(),
                            UserId = user.Id,
                            RoleId = adminRole.Id,
                            AssignedAt = DateTime.UtcNow,
                            IsActive = true
                        });
                        await db.SaveChangesAsync();
                        logger.LogInformation("[Startup] Created initial admin user from environment (USERNAME={Username}, EMAIL={Email})", adminUser, adminEmail);
                    }
                    else
                    {
                        logger.LogWarning("[Startup] Cannot create admin user from environment because farm_admin role not found.");
                    }
                }
                else
                {
                    logger.LogWarning("[Startup] ENABLE_ADMIN_BOOTSTRAP=true but ADMIN_* variables missing or password policy not met (>=12 chars). Skipping.");
                }
            }
            else
            {
                logger.LogDebug("[Startup] ENABLE_ADMIN_BOOTSTRAP=true but admin already exists; no action taken.");
            }
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADMIN_USERNAME")))
        {
            logger.LogWarning("[Startup] ADMIN_* variables detected but ENABLE_ADMIN_BOOTSTRAP!=true. Bootstrap skipped by design.");
        }
    }
    catch (Exception ex)
    {
        var logger2 = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger2.LogWarning(ex, "[Startup] Admin bootstrap attempt failed (non-fatal)");
    }
}

// Log effective temp root (non-production) for diagnostics
try
{
    var env = app.Services.GetRequiredService<IHostEnvironment>();
    if (!env.IsProduction())
    {
        var tempProvider = app.Services.GetRequiredService<ITempPathProvider>();
        app.Logger.LogInformation("[Startup] Temp root: {TempRoot}", tempProvider.GetTempRoot());
    }
}
catch { /* ignore diagnostics failure */ }

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
app.MapPost("/api/network-discovery/settings", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] ([FromServices] INetworkDiscoverySettingsService svc, [FromBody] NetworkDiscoverySettingsDto body) => { svc.SaveSettings(body); return Results.NoContent(); });
app.MapPost("/api/network-discovery/auto-detect", [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")] () =>
{
    // Enumerate local IPv4 addresses and suggest /24 CIDR blocks.
    var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }
            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    // If subnet mask available, derive CIDR; fallback to /24.
                    int prefix = 24;
                    if (ua.IPv4Mask is not null)
                    {
                        var maskBytes = ua.IPv4Mask.GetAddressBytes();
                        var ones = 0;
                        foreach (var b in maskBytes)
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
                    var networkBytes = ua.Address.GetAddressBytes();
                    if (prefix <= 32 && prefix >= 8)
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
                        var networkBase = new System.Net.IPAddress(networkBytes);
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
    var rangesEnv = Environment.GetEnvironmentVariable("DISCOVERY_RANGES");
    var portsEnv = Environment.GetEnvironmentVariable("DISCOVERY_PORTS");
    var current = svc.GetSettings();
    List<string> ranges = current.NetworkRanges;
    if (!string.IsNullOrWhiteSpace(rangesEnv))
    {
        ranges = [.. rangesEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
    }
    List<int> ports = current.Ports;
    if (!string.IsNullOrWhiteSpace(portsEnv))
    {
        ports = [.. portsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var v) ? v : -1)
            .Where(v => v > 0 && v < 65536)
            .Distinct()];
        if (ports.Count == 0)
        {
            ports = current.Ports;
        }
    }
    var updated = new NetworkDiscoverySettingsDto(ranges, current.TimeoutMs, current.MaxConcurrentScans, ports);
    svc.SaveSettings(updated);
    return Results.Ok(updated);
});

// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
// Extended diagnostic: expose active temp root (non-sensitive path) for debugging; omit if running in Production
app.MapGet("/diagnostics/temp-root", (Microsoft.AspNetCore.Hosting.IWebHostEnvironment env, Farm.Web.Api.Infrastructure.Temp.ITempPathProvider provider) =>
{
    if (env.IsProduction())
    {
        return Results.StatusCode(StatusCodes.Status404NotFound);
    }
    return Results.Ok(new { tempRoot = provider.GetTempRoot() });
});
// Combined diagnostics (non-sensitive) for UI consumption
app.MapGet("/api/diagnostics/summary", ([FromServices] SpoolmanService spoolmanSvc, [FromServices] INetworkDiscoverySettingsService discoverySvc) =>
{
    var spoolCfg = spoolmanSvc.GetConfig();
    var discovery = discoverySvc.GetSettings();
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

// Database info endpoint (dev or DEBUG_DB_INFO=true) with migration status integration.
app.MapGet("/api/debug/db-info", async (AppDbContext db,
    IWebHostEnvironment env,
    IConfiguration config,
    [Microsoft.AspNetCore.Mvc.FromServices] Farm.Web.Api.Infrastructure.Database.IMigrationStatusProvider migrationStatusProvider,
    CancellationToken ct) =>
{
    var toggle = (Environment.GetEnvironmentVariable("DEBUG_DB_INFO") ?? config["DEBUG_DB_INFO"])?.Trim();
    var allow = env.IsDevelopment() || (toggle != null && toggle.Equals("true", StringComparison.OrdinalIgnoreCase));
    if (!allow)
    {
        return Results.NotFound();
    }

    var provider = db.Database.ProviderName ?? "unknown";
    string databaseName;
    try
    {
        databaseName = db.Database.GetDbConnection().Database;
    }
    catch
    {
        databaseName = "unknown";
    }

    var entities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
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
        [nameof(db.DiscoveredGcodeFiles)] = await db.DiscoveredGcodeFiles.CountAsync(ct),
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
            var cs = db.Database.GetConnectionString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cs))
            {
                var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs);
                var dataSource = builder.DataSource;
                if (!Path.IsPathRooted(dataSource))
                {
                    dataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataSource));
                }
                if (File.Exists(dataSource))
                {
                    fileSizeBytes = new System.IO.FileInfo(dataSource).Length;
                }
            }
        }
        catch { }
    }

    var migration = migrationStatusProvider.GetStatus();

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
    var staticRoot = app.Environment.WebRootPath;
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
