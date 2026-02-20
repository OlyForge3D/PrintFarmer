using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Host;
using Farm.Slicer.Host.Services;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Api;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

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

// ── Remaining stubs + real implementations for standalone host ─────────────────
// IModel3DFileService is stubbed (implementation lives in API with deep dependencies).
// I3MfToStlConversionService uses real implementation from Farm.Infrastructure.
builder.Services.AddUnimplementedSlicerServiceStubs();

// ── Infrastructure services shared with the main API ──────────────────────────
builder.Services.AddSingleton<IUnifiedLoggingService, UnifiedLoggingService>();

// ── Authentication (transitional — allow all for standalone mode) ──────────────
// When the host is deployed behind an API gateway, this will be replaced with
// proper JWT/API-key authentication forwarded from the gateway.
builder.Services
    .AddAuthentication("StandaloneScheme")
    .AddScheme<AuthenticationSchemeOptions, StandaloneAuthHandler>(
        "StandaloneScheme", null);
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("farm_admin", policy => policy.RequireAssertion(_ => true));
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

await app.RunAsync();

/// <summary>Marker type so integration tests can reference the host assembly.</summary>
#pragma warning disable S1118 // Utility classes should not have public constructors — marker type for WebApplicationFactory
public partial class Program
{
}
#pragma warning restore S1118
