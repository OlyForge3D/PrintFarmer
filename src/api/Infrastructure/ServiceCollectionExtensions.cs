using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Services.DiscoveryProbes;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.SlicerServices;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace Farm.Web.Api.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPrintFarmerDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        string provider = configuration.GetValue<string>("DB_PROVIDER")?.ToLower() ?? "sqlite";
        string connectionStringKey = provider switch
        {
            "sqlserver" => "SqlServer",
            "postgres" or "postgresql" => "Postgres",
            "mysql" => "MySql",
            _ => "Default"
        };
        string connectionString = configuration.GetConnectionString(connectionStringKey)
            ?? configuration.GetValue<string>("DB_CONNECTION")
            ?? "Data Source=farm.db";

        _ = services.AddDbContext<AppDbContext>(options =>
        {
            switch (provider)
            {
                case "sqlserver":
                    _ = options.UseSqlServer(connectionString);
                    break;
                case "postgres":
                case "postgresql":
                    _ = options.UseNpgsql(connectionString);
                    break;
                case "mysql":
                    _ = options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
                    break;
                default:
                    _ = options.UseSqlite(connectionString);
                    break;
            }
        });

        return services;
    }

    public static IServiceCollection AddPrintFarmerSettings(this IServiceCollection services)
    {
        _ = services.AddScoped<SettingsService>(sp =>
            new SettingsService(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<AppDbContext>(),
                sp.GetRequiredService<IUnifiedLoggingService>()));
        _ = services.AddScoped<ISettingsService>(sp =>
            sp.GetRequiredService<SettingsService>());

        return services;
    }

    public static IServiceCollection AddPrintFarmerServices(this IServiceCollection services)
    {
        // Caching
        _ = services.AddMemoryCache();
        _ = services.AddOptions<CatalogCacheOptions>();
        _ = services.AddScoped<ICatalogCache, CatalogCache>();

        // API Clients
        _ = services.AddScoped<MoonrakerClient>();
        _ = services.AddScoped<PrusaLinkClient>();
        _ = services.AddScoped<OctoPrintClient>();
        _ = services.AddScoped<SdcpClient>();

        // Discovery Services
        _ = services.AddAllNetworkDiscoveryProbes();
        _ = services.AddSingleton<IDiscoveryProgressCache, DiscoveryProgressCache>();
        _ = services.AddScoped<INetworkDiscoveryService, NetworkDiscoveryService>();
        _ = services.AddScoped<IPrinterCapabilityDiscoveryService, PrinterCapabilityDiscoveryService>();

        // Business Services
        _ = services.AddScoped<IDefaultCatalogService, DefaultCatalogService>();
        _ = services.AddScoped<ICircuitBreakerService, CircuitBreakerService>();
        _ = services.AddScoped<SystemLogCleanupService>();
        _ = services.AddScoped<DatabaseInitializer>();
        _ = services.AddScoped<NetworkUrlRewriteService>();

        // Telemetry and Logging
        ActivitySource activitySource = new("PrintFarmer.API");
        _ = services.AddSingleton(_ => activitySource);
        _ = services.AddScoped<IPrintFarmerTelemetryService, PrintFarmerTelemetryService>();
        _ = services.AddScoped<IUnifiedLoggingService, UnifiedLoggingService>();
        _ = services.AddScoped<Farm.Infrastructure.Normalization.INormalizationEventLogger, Farm.Infrastructure.Normalization.NormalizationEventLogger>();

        // HTTP Clients with typed clients
        _ = services.AddHttpClient<IMoonrakerClient, MoonrakerClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        _ = services.AddHttpClient<IPrusaLinkClient, PrusaLinkClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        _ = services.AddHttpClient<IOctoPrintClient, OctoPrintClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        _ = services.AddHttpClient<ISdcpClient, SdcpClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Spoolman Integration
        _ = services.AddScoped<ISpoolmanService, SpoolmanService>();
        _ = services.AddHttpClient<SpoolmanService>("SpoolmanService", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Authentication
        _ = services.AddScoped<IPasswordHashingService, PasswordHashingService>();
        _ = services.AddScoped<IAuthenticationService, AuthenticationService>();

        // Startup tracking
        _ = services.AddSingleton<StartupStatus>();

        // Harvest queue and gcode harvest service
        _ = services.AddScoped<IHarvestQueue, InMemoryHarvestQueue>();
        _ = services.AddScoped<IGcodeHarvestService, GcodeHarvestService>();

        return services;
    }
}
