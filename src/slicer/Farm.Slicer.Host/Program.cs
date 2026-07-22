using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Authorization;
using Farm.Slicer.Host;
using Farm.Slicer.Host.Services;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Api;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("PFARM__");

// ── Database ──────────────────────────────────────────────────────────────────
// SlicerDbContext (module-owned; multi-provider: SQLite, PostgreSQL, SQL Server)
builder.Services.AddSlicerModule(builder.Configuration);

// ── Slicer API-layer services (real implementations from Farm.Slicer.Module.Api) ──
// Registers controllers' service dependencies: SlicersService, SlicingSubmissionService,
// ArtifactsService, ProfilesService, WorkerAuthService, file storage, job dispatch, etc.
builder.Services.AddSlicerApiServices(builder.Configuration);

// ── Cross-domain lookup services (HTTP → main API) ───────────────────────────
// Resolves printers, catalog models, and manufacturer names from the main API
// via REST calls with in-memory caching. Must come AFTER AddSlicerApiServices so
// that HttpCatalogServiceAdapter and HttpPrinterLookupService take precedence over
// the module-local adapters registered above.
builder.Services.AddCrossDomainLookupServices(builder.Configuration);

// ── Shared infrastructure services (AppDbContext, tags, catalog, settings) ────
// In standalone mode, the slicer-host shares the same PostgreSQL database
// as the main API and registers infrastructure services locally rather than
// proxying all calls over HTTP.
builder.Services.AddSharedInfrastructureServices(builder.Configuration);

// ── Slicer service implementations for standalone host ────────────────────────
// IModel3DFileService (Farm.Slicer.Module) and I3MfToStlConversionService (Farm.Infrastructure).
builder.Services.AddUnimplementedSlicerServiceStubs();

// ── Infrastructure services shared with the main API ──────────────────────────
// ILogger<T> is automatically provided by the DI container

// ── Authentication ─────────────────────────────────────────────────────────
// Standalone mode auto-authenticates every request as admin (transitional; see
// StandaloneAuthHandler). When Jwt:Key is configured (the host shares JWT
// signing configuration with the main API, e.g. because Desktop-exchanged
// tokens from issue #838 may be presented, or the host is deployed behind
// the same gateway as the main API), a JWT bearer scheme is also registered.
// A policy scheme forwards requests carrying a Bearer Authorization header
// to the JWT handler and everything else to the standalone admin handler,
// so existing standalone deployments (no Jwt:Key configured) are completely
// unaffected.
string? jwtKey = builder.Configuration["Jwt:Key"];
bool jwtConfigured = !string.IsNullOrWhiteSpace(jwtKey) && jwtKey.Length >= 32;

AuthenticationBuilder authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "SmartScheme";
    options.DefaultAuthenticateScheme = "SmartScheme";
});

authBuilder.AddScheme<AuthenticationSchemeOptions, StandaloneAuthHandler>("StandaloneScheme", null);

if (jwtConfigured)
{
    string issuer = builder.Configuration["Jwt:Issuer"] ?? "PrintFarmer";
    string audience = builder.Configuration["Jwt:Audience"] ?? "PrintFarmer";

    authBuilder.AddJwtBearer("Bearer", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey!)),
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // SignalR's WebSocket / Server-Sent-Events transports cannot set the Authorization
        // header on the browser handshake, so the client sends the JWT as a ?access_token=
        // query parameter instead. Honour it for hub paths (e.g. /hubs/slicers); without this
        // the WS upgrade to the [Authorize] hub is rejected 401 and SignalR silently downgrades
        // to long-polling. The negotiate POST still uses the Authorization header (default).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Authorization header still takes precedence (mirrors the main API): only fall
                // back to the query token when the header didn't already supply one.
                if (string.IsNullOrEmpty(context.Token))
                {
                    string? token = SlicerHubAuth.ResolveHubAccessToken(context.Request);
                    if (token is not null)
                    {
                        context.Token = token;
                    }
                }

                return Task.CompletedTask;
            },
        };
    });

    authBuilder.AddPolicyScheme("SmartScheme", "Bearer token or standalone admin", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            string? authHeader = context.Request.Headers.Authorization.ToString();
            return !string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? "Bearer"
                : "StandaloneScheme";
        };
    });
}
else
{
    authBuilder.AddPolicyScheme("SmartScheme", "Standalone admin (Jwt:Key not configured)", options =>
    {
        options.ForwardDefaultSelector = _ => "StandaloneScheme";
    });
}

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("farm_admin", policy => policy.RequireRole("farm_admin"));

    // Desktop API-key exchange scope policies (issue #838). The standalone admin
    // principal carries no token_use claim, so DesktopScopeAuthorizationHandler
    // passes it through unchanged - existing standalone deployments are unaffected.
    opts.AddPolicy("ModelRead", policy => policy.AddRequirements(new DesktopScopeRequirement("ModelRead")));
    opts.AddPolicy("ModelWrite", policy => policy.AddRequirements(new DesktopScopeRequirement("ModelWrite")));
    opts.AddPolicy("LibrarySync", policy => policy.AddRequirements(new DesktopScopeRequirement("LibrarySync")));
});
builder.Services.AddSingleton<IAuthorizationHandler, DesktopScopeAuthorizationHandler>();

// ── JSON serialisation ────────────────────────────────────────────────────────
Action<JsonSerializerOptions> configureJson = o =>
{
    o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.Converters.Add(new JsonStringEnumConverter());
};

// ── Controllers (slicer module only) ──────────────────────────────────────────
builder.Services
    .AddControllers()
    .AddSlicerControllers()
    .AddJsonOptions(o => configureJson(o.JsonSerializerOptions));

// ── SignalR ───────────────────────────────────────────────────────────────────
builder.Services.AddSignalR()
    .AddJsonProtocol(o => configureJson(o.PayloadSerializerOptions));

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SlicerDbContext>("slicer-db");

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
#pragma warning disable S5122 // slicer-host is internal and reached same-origin via nginx; all internal LAN origins are intentional, and restriction breaks direct LAN access.
        _ = policy.AllowAnyOrigin();
#pragma warning restore S5122
        _ = policy.AllowAnyMethod();
        _ = policy.AllowAnyHeader();
    }));

// ── Bind port ─────────────────────────────────────────────────────────────────
#pragma warning disable S1075
builder.WebHost.UseUrls("http://0.0.0.0:5246");
#pragma warning restore S1075

WebApplication app = builder.Build();

// Configure artifact storage metrics thresholds and alert subscriptions.
app.ConfigureSlicerMetrics();

// Ensure slicer database schema exists on startup
using (IServiceScope scope = app.Services.CreateScope())
{
    SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
    string providerName = db.Database.ProviderName ?? string.Empty;
    bool isSqlite = providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

    if (isSqlite)
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapSlicerHubs();
app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new { service = "Farm.Slicer.Host", status = "running" }));

// Build version endpoint
app.MapGet("/api/system/version", () =>
{
    var asm = System.Reflection.Assembly.GetEntryAssembly();
    string? infoVersion = (asm is not null
        ? Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
            as System.Reflection.AssemblyInformationalVersionAttribute
        : null)?.InformationalVersion;
    string version = "0.0.0";
    string? commit = null;
    if (infoVersion != null)
    {
        string[] parts = infoVersion.Split('+', 2);
        version = parts[0];
        commit = parts.Length > 1 ? parts[1] : null;
    }

    return Results.Ok(new
    {
        service = "Farm.Slicer.Host",
        version,
        commit,
        environment = app.Environment.EnvironmentName,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        timestamp = DateTime.UtcNow,
    });
});

await app.RunAsync();

/// <summary>Marker type so integration tests can reference the host assembly.</summary>
#pragma warning disable S1118 // Utility classes should not have public constructors — marker type for WebApplicationFactory
public partial class Program
{
}
#pragma warning restore S1118
