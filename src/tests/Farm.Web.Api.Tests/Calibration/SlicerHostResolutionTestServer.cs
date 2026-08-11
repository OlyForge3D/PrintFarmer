using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Controllers.Calibration;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Api.Health;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Boots the production slicer-host calibration resolution endpoint (real controller, real
/// permission filter, real database-backed <see cref="CalibrationProfileResolver"/> and the real
/// availability probe) on an in-process HTTP test server.
/// </summary>
/// <remarks>
/// Split-deployment tests point the main API's resolver adapter at this server so the whole hop —
/// bearer forwarding, authorization, ownership scoping and JSON contract — is exercised over real
/// HTTP instead of being replaced by a mock.
/// </remarks>
internal sealed class SlicerHostResolutionTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SlicerHostResolutionTestServer(WebApplication app) => _app = app;

    /// <summary>Starts a slicer host bound to the supplied SQLite profile store.</summary>
    /// <param name="connectionString">Connection string of the shared test database.</param>
    /// <param name="jwtKey">Signing key shared with the main API, as compose requires.</param>
    /// <param name="issuer">JWT issuer shared with the main API.</param>
    /// <param name="audience">JWT audience shared with the main API.</param>
    /// <returns>The started slicer host.</returns>
    public static async Task<SlicerHostResolutionTestServer> StartAsync(
        string connectionString,
        string jwtKey,
        string issuer,
        string audience)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();

        _ = builder.Services.AddDbContext<SlicerDbContext>(options =>
            options.UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite")));
        _ = builder.Services.AddScoped<ICalibrationProfileResolver, CalibrationProfileResolver>();
        _ = builder.Services.AddScoped<IPermissionValidator, ClaimsPermissionValidator>();

        _ = builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        _ = builder.Services.AddAuthorization();

        _ = builder.Services
            .AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new CalibrationResolutionApplicationPart());
            })
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        _ = builder.Services.AddHealthChecks()
            .AddCheck<CalibrationProfileResolverHealthCheck>(
                CalibrationProfileResolutionContract.HealthCheckName,
                tags: [CalibrationProfileResolutionContract.HealthCheckTag]);

        WebApplication app = builder.Build();
        _ = app.UseAuthentication();
        _ = app.UseAuthorization();
        _ = app.MapControllers();
        _ = app.MapHealthChecks(
            "/" + CalibrationProfileResolutionContract.HealthRelativeRoute,
            new HealthCheckOptions
            {
                Predicate = registration =>
                    registration.Tags.Contains(CalibrationProfileResolutionContract.HealthCheckTag),
            });

        await app.StartAsync();
        return new SlicerHostResolutionTestServer(app);
    }

    /// <summary>Creates the message handler the API's typed resolver client dials through.</summary>
    public HttpMessageHandler CreateHandler() => _app.GetTestServer().CreateHandler();

    /// <summary>Creates a direct client for endpoint-level assertions.</summary>
    public HttpClient CreateClient() => _app.GetTestServer().CreateClient();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// Exposes only the calibration resolution controller, so the rest of the slicer API surface
    /// (which needs services a resolution host does not have) stays out of this test server.
    /// </summary>
    private sealed class CalibrationResolutionApplicationPart : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => nameof(CalibrationResolutionApplicationPart);

        public IEnumerable<TypeInfo> Types { get; } =
            [typeof(CalibrationProfileResolutionController).GetTypeInfo()];
    }
}
