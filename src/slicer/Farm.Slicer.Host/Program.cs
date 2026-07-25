using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Slicer.Host;
using Farm.Slicer.Host.Services;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Api;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// ── Authentication ─────────────────────────────────────────────────────────────
string jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException(
        "JWT Key not configured. Provide Jwt__Key using the same secret as the main PrintFarmer API.");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsEnvironment("Testing");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "PrintFarmer",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "PrintFarmer",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs") &&
                    context.Request.Query.TryGetValue(
                        "access_token",
                        out Microsoft.Extensions.Primitives.StringValues accessToken))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("farm_admin", policy =>
    {
        _ = policy.RequireAuthenticatedUser();
        _ = policy.RequireRole("farm_admin");
    });
});

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
    .AddDbContextCheck<AppDbContext>("core-db")
    .AddDbContextCheck<SlicerDbContext>("slicer-db");

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// ── Bind port ─────────────────────────────────────────────────────────────────
#pragma warning disable S1075
builder.WebHost.UseUrls("http://0.0.0.0:5246");
#pragma warning restore S1075

WebApplication app = builder.Build();

// Configure artifact storage metrics thresholds and alert subscriptions.
app.ConfigureSlicerMetrics();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapSlicerHubs();
app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new { service = "Farm.Slicer.Host", status = "running" }));

await app.RunAsync();

/// <summary>Marker type so integration tests can reference the host assembly.</summary>
#pragma warning disable S1118 // Utility classes should not have public constructors — marker type for WebApplicationFactory
public partial class Program
{
}
#pragma warning restore S1118
