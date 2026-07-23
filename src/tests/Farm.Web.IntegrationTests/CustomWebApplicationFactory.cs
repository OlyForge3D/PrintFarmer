using System;
using System.Net;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Farm.Web.IntegrationTests;

// Minimal compatibility CustomWebApplicationFactory for integration tests
// Provides a simple WebApplicationFactory<Program> so tests can run in this environment.
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _hostConfiguration;
    private readonly SqliteConnection _keepAliveConnection;
    private int _keepAliveDisposed;

    public CustomWebApplicationFactory()
    {
        string factoryId = Guid.NewGuid().ToString("N");
        _keepAliveConnection = new SqliteConnection(
            $"Data Source=file:integ_shared_{factoryId}?mode=memory&cache=shared;Pooling=False");
        _keepAliveConnection.Open();

        ConnectionString = _keepAliveConnection.ConnectionString;
        JwtSigningKey = $"test-integration-signing-key-{factoryId}";
        JwtIssuer = $"PrintFarmer-{factoryId}";
        JwtAudience = $"PrintFarmer-{factoryId}";
        _hostConfiguration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = ConnectionString,
            ["ConnectionStrings:Sqlite"] = ConnectionString,
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            ["ConnectionStrings:SlicerDatabase"] = ConnectionString,
            ["TEST_USE_SQLITE_INMEMORY"] = "true",
            ["TEST_DISABLE_BACKGROUND_SERVICES"] = "true",
            ["DISABLE_TELEMETRY"] = "true",
            ["Jwt:Enabled"] = "true",
            ["Jwt:Key"] = JwtSigningKey,
            ["Jwt:Issuer"] = JwtIssuer,
            ["Jwt:Audience"] = JwtAudience,
        };
    }

    internal string ConnectionString { get; }
    internal string JwtSigningKey { get; }
    internal string JwtIssuer { get; }
    internal string JwtAudience { get; }

    public static CustomWebApplicationFactory CreateWithIsolatedDatabase(bool useInMemorySqlite = true)
    {
        _ = useInMemorySqlite;
        return new CustomWebApplicationFactory();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Trust loopback as a forwarded-headers proxy so integration tests can supply
        // X-Forwarded-For to isolate per-test rate-limit buckets. In TestServer the
        // Connection.RemoteIpAddress is null by default; the LoopbackConnectionStartupFilter
        // below sets it to 127.0.0.1 at the very start of the pipeline so the framework's
        // ForwardedHeadersMiddleware sees a trusted proxy and rewrites RemoteIpAddress from
        // the X-Forwarded-For value. This exercises the same trust path a production
        // reverse-proxy deployment uses (regression protection for issue #862).
        _ = builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:Enabled"] = "true",
                ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
                ["ForwardedHeaders:KnownProxies:1"] = "::1",
                ["ForwardedHeaders:ForwardLimit"] = "1",
            });
        });

        _ = builder.ConfigureServices((context, services) =>
        {
            // This callback runs while the minimal host is building, after Program has
            // added its services. Capture branch outcomes before the provider is created
            // so tests do not mistake final IConfiguration values for startup-time proof.
            _ = services.AddSingleton(new StartupRegistrationSnapshot(
                context.HostingEnvironment.EnvironmentName,
                services.Any(descriptor =>
                    descriptor.ServiceType == typeof(TracerProvider)
                    || descriptor.ServiceType == typeof(MeterProvider)),
                GuardedBackgroundServiceTypes.Any(type => HasRegistration(services, type))));

            // Minimal-host service registration runs before WebApplicationFactory's
            // app-configuration callback. Register the production options again here,
            // after the host-scoped provider above is visible, so no process-global
            // environment mutation is needed.
            _ = services.AddPrintFarmerForwardedHeaders(context.Configuration);

            // Provide a lightweight test orchestrator so integration tests can run without full worker infrastructure.
            _ = services.AddSingleton<ISlicerOrchestrator, TestSlicerOrchestrator>();
            _ = services.AddSingleton<IStartupFilter, LoopbackConnectionStartupFilter>();
        });
    }

    /// <summary>
    /// Ensures <c>Connection.RemoteIpAddress</c> is populated with the IPv4 loopback
    /// address at the very start of the pipeline. TestServer leaves it null by default,
    /// which prevents <c>UseForwardedHeaders</c> from recognizing the request as coming
    /// from a trusted proxy and honoring <c>X-Forwarded-For</c>.
    /// </summary>
    private sealed class LoopbackConnectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                _ = app.Use(async (ctx, nxt) =>
                {
                    ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    await nxt();
                });
                next(app);
            };
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Microsoft.AspNetCore.Mvc.Testing's minimal-host DeferredHostBuilder turns
        // host configuration into entry-point command-line arguments. This is the
        // supported per-host path that reaches WebApplication.CreateBuilder(args)
        // before Program makes service-registration decisions.
        _ = builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(_hostConfiguration));

        // Keep the environment host-scoped. Rate limiting remains enabled so the
        // integration suite exercises the production middleware.
        _ = builder.UseEnvironment("Testing");

        IHost host = base.CreateHost(builder);

        // Best-effort: run a host-scoped EnsureCreated + DatabaseInitializer.SeedAllAsync
        // so integration tests see the same seed data and schema as other factories.
        try
        {
            using IServiceScope scope = host.Services.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = db.Database.EnsureCreated();

            DatabaseInitializer? initializer = scope.ServiceProvider.GetService<DatabaseInitializer>();
            if (initializer != null)
            {
                try
                {
                    initializer.InitializeAsync("sqlite", 3, 2).GetAwaiter().GetResult();
                    initializer.SeedAllAsync().GetAwaiter().GetResult();
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }

        return host;
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (Interlocked.Exchange(ref _keepAliveDisposed, 1) == 0)
            {
                await _keepAliveConnection.DisposeAsync();
            }
        }
    }

    private static bool HasRegistration(IServiceCollection services, Type implementationType)
        => services.Any(descriptor =>
            descriptor.ServiceType == implementationType
            || descriptor.ImplementationType == implementationType);

    private static readonly Type[] GuardedBackgroundServiceTypes =
    [
        typeof(Farm.Infrastructure.Services.GcodeHarvest.GcodeHarvestQueueProcessorService),
        typeof(Farm.Infrastructure.Services.SystemLogs.SystemLogCleanupService),
        typeof(Farm.Web.Api.Services.Workers.DiscoveryHeartbeatMonitorService),
        typeof(Farm.Infrastructure.Services.Queue.Dispatch.AutoDispatchBackgroundService),
        typeof(Farm.Infrastructure.Services.Cameras.CameraHealthMonitorService),
        typeof(Farm.Infrastructure.Services.FailureDetection.PrintFailureMonitorService),
    ];

    internal sealed record StartupRegistrationSnapshot(
        string EnvironmentName,
        bool TelemetryRegistered,
        bool GuardedBackgroundServicesRegistered);
}
