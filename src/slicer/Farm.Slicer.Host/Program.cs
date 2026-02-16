using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
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

// ── Cross-domain lookup services (HTTP → main API) ───────────────────────────
// Resolves printers, catalog models, and manufacturer names from the main API
// via REST calls with in-memory caching.
builder.Services.AddCrossDomainLookupServices(builder.Configuration);

// ── Slicer services (stub implementations — transitional) ─────────────────────
// Once real implementations are migrated into Farm.Slicer.Module, this call
// will be replaced by AddSlicerServices().
builder.Services.AddSlicerStubServices();

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

// Ensure slicer database schema exists on startup
using (IServiceScope scope = app.Services.CreateScope())
{
    SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
    await db.Database.EnsureCreatedAsync();
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
public partial class Program
{
}
