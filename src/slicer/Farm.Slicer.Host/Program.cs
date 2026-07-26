using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Slicer.Host;
using Farm.Slicer.Host.Services;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Api;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("PFARM__");

// SlicerDbContext (module-owned; multi-provider: SQLite, PostgreSQL, SQL Server).
builder.Services.AddSlicerModule(builder.Configuration);

// Slicer API-layer services (real implementations from Farm.Slicer.Module.Api).
builder.Services.AddSlicerApiServices(builder.Configuration);

// Cross-domain lookup services must follow AddSlicerApiServices so the HTTP adapters win.
builder.Services.AddCrossDomainLookupServices(builder.Configuration);

// Standalone slicer-host shares the core database and infrastructure services.
builder.Services.AddSharedInfrastructureServices(builder.Configuration);

builder.Services.AddUnimplementedSlicerServiceStubs();

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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("farm_admin", policy =>
    {
        _ = policy.RequireAuthenticatedUser();
        _ = policy.RequireRole("farm_admin");
    });

    // Desktop exchange tokens remain scope-gated; regular JWTs pass these policies.
    options.AddPolicy("ModelRead", policy => policy.AddRequirements(new DesktopScopeRequirement("ModelRead")));
    options.AddPolicy("ModelWrite", policy => policy.AddRequirements(new DesktopScopeRequirement("ModelWrite")));
    options.AddPolicy("LibrarySync", policy => policy.AddRequirements(new DesktopScopeRequirement("LibrarySync")));
});
builder.Services.AddSingleton<IAuthorizationHandler, DesktopScopeAuthorizationHandler>();

Action<JsonSerializerOptions> configureJson = options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.Converters.Add(new JsonStringEnumConverter());
};

builder.Services
    .AddControllers()
    .AddSlicerControllers()
    .AddJsonOptions(options => configureJson(options.JsonSerializerOptions));

builder.Services.AddSignalR()
    .AddJsonProtocol(options => configureJson(options.PayloadSerializerOptions));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("core-db")
    .AddDbContextCheck<SlicerDbContext>("slicer-db");

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
#pragma warning disable S5122 // Internal same-origin gateway plus supported direct LAN access require this host policy.
        _ = policy.AllowAnyOrigin();
#pragma warning restore S5122
        _ = policy.AllowAnyMethod();
        _ = policy.AllowAnyHeader();
    }));

#pragma warning disable S1075
builder.WebHost.UseUrls("http://0.0.0.0:5246");
#pragma warning restore S1075

WebApplication app = builder.Build();

app.ConfigureSlicerMetrics();

// ── Slicer plugin sanity check (issue #578) ───────────────────────────────────
// slicer-host is useless without at least one slicer library plugin — a config
// or Docker layout regression that ships an empty plugins dir would otherwise
// present as "no engines" in the API with no clear cause. Fail fast at startup
// so the container restarts and the deployment problem is visible.
using (IServiceScope scope = app.Services.CreateScope())
{
    Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry registry =
        scope.ServiceProvider.GetRequiredService<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>();
    int count = registry.ListAllLibraries().Count();
    if (count == 0)
    {
        string pluginsPath = builder.Configuration["Slicer:PluginsPath"] ?? "(unset)";
        throw new InvalidOperationException(
            "slicer-host started with zero registered slicer libraries. " +
            $"Slicer:PluginsPath={pluginsPath}. Ensure the container image includes " +
            "Farm.Slicers.OrcaSlicer.v2_4_0.dll / v2_3_1.dll in the plugins directory.");
    }
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapSlicerHubs();
app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new { service = "Farm.Slicer.Host", status = "running" }));

app.MapGet("/api/system/version", () =>
{
    var assembly = System.Reflection.Assembly.GetEntryAssembly();
    string? informationalVersion = (assembly is not null
        ? Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
            as System.Reflection.AssemblyInformationalVersionAttribute
        : null)?.InformationalVersion;
    string version = "0.0.0";
    string? commit = null;
    if (informationalVersion is not null)
    {
        string[] parts = informationalVersion.Split('+', 2);
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
#pragma warning disable S1118 // Utility classes should not have public constructors - marker type for WebApplicationFactory.
public partial class Program
{
}
#pragma warning restore S1118
