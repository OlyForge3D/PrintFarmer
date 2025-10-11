using System;
using System.Diagnostics;
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

namespace Farm.Web.Api.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPrintFarmerDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        // Read provider value without forcing culture-sensitive lowercasing.
        // We'll trim and perform case-insensitive comparisons where needed.
        string? providerRaw = configuration.GetValue<string>("DB_PROVIDER");
        string provider = string.IsNullOrWhiteSpace(providerRaw) ? "sqlite" : providerRaw.Trim();

        // Always use "Default" connection string key for all providers
        string connectionString = configuration.GetConnectionString("Default")
            ?? configuration.GetValue<string>("DB_CONNECTION")
            ?? "Data Source=farm.db";

        _ = services.AddDbContext<AppDbContext>(options =>
        {
            if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                _ = options.UseSqlServer(connectionString);
            }
            else if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
            {
                _ = options.UseNpgsql(connectionString);
            }
            else if (provider.Equals("mysql", StringComparison.OrdinalIgnoreCase))
            {
                _ = options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
            }
            else
            {
                _ = options.UseSqlite(connectionString);
            }
        });

        // Also register a DbContextFactory for creating short-lived AppDbContext instances from singletons
        _ = services.AddDbContextFactory<AppDbContext>(options =>
        {
            if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                _ = options.UseSqlServer(connectionString);
            }
            else if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
            {
                _ = options.UseNpgsql(connectionString);
            }
            else if (provider.Equals("mysql", StringComparison.OrdinalIgnoreCase))
            {
                _ = options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
            }
            else
            {
                _ = options.UseSqlite(connectionString);
            }
        });

        return services;
    }

    public static IServiceCollection AddPrintFarmerSettings(this IServiceCollection services)
    {
        // Register configuration-bound system settings (no DB access required)
        // Bind DatabaseSettings from configuration section name defined on the type
        try
        {
            // Use the SectionName constant if present
            _ = services.Configure<Farm.Infrastructure.Settings.DatabaseSettings>(s => { });
        }
        catch { }

        // Register a lightweight provider for system settings that reads from IConfiguration
        _ = services.AddSingleton<Farm.Infrastructure.Settings.ISystemSettingsProvider, Farm.Infrastructure.Settings.ConfigurationSystemSettingsProvider>();

        // Register SettingsService so DI constructs it with IConfiguration, AppDbContext and IUnifiedLoggingService
        _ = services.AddScoped<ISettingsService, SettingsService>();

        // Settings initialization from environment variables (scoped to match ISettingsService)
        // Register settings initialization via its interface
        _ = services.AddScoped<Farm.Infrastructure.Settings.ISettingsInitializationService, SettingsInitializationService>();

        return services;
    }

    public static IServiceCollection AddPrintFarmerServices(this IServiceCollection services)
    {
        // Caching
        _ = services.AddMemoryCache();
        _ = services.AddOptions<CatalogCacheOptions>();
        // CatalogCache is implemented to resolve a scoped AppDbContext per-call, so it can be a Singleton
        _ = services.AddSingleton<ICatalogCache, CatalogCache>();

        // API Clients
        // Use typed HttpClient registrations below (IMoonrakerClient, IPrusaLinkClient, IOctoPrintClient, ISdcpClient)
        // Avoid duplicate raw scoped registrations for the concrete client types.

        // Discovery Services
        _ = services.AddAllNetworkDiscoveryProbes();
        _ = services.AddSingleton<IDiscoveryProgressCache, DiscoveryProgressCache>();
        _ = services.AddScoped<INetworkDiscoveryService, NetworkDiscoveryService>();
        _ = services.AddScoped<IPrinterCapabilityDiscoveryService, PrinterCapabilityDiscoveryService>();

        // Business Services
        _ = services.AddScoped<IDefaultCatalogService, DefaultCatalogService>();
        _ = services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();
        // SystemLogCleanupService is a background worker; register as hosted service
        _ = services.AddHostedService<SystemLogCleanupService>();
        _ = services.AddScoped<DatabaseInitializer>();
        _ = services.AddScoped<Farm.Web.Api.Services.Interfaces.IDatabaseInitializer, DatabaseInitializer>();
        // NetworkUrlRewriteService is stateless and depends on IConfiguration and logging - safe as a Singleton
        // Register NetworkUrlRewriteService as the implementation for INetworkUrlRewriteService
        _ = services.AddSingleton<INetworkUrlRewriteService, NetworkUrlRewriteService>();

        // Telemetry and Logging
        ActivitySource activitySource = new("PrintFarmer.API");
        _ = services.AddSingleton(_ => activitySource);
        // Telemetry service is thread-safe and manages Meter/ActivitySource lifetimes – register as Singleton
        _ = services.AddSingleton<IPrintFarmerTelemetryService, PrintFarmerTelemetryService>();
        // IUnifiedLoggingService must be Singleton because it's used by Singleton services like IHarvestQueue
        // It only depends on ILogger (Singleton) and IServiceProvider (Singleton), so this is safe
        _ = services.AddSingleton<IUnifiedLoggingService, UnifiedLoggingService>();
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
        // Provide typed HttpClient for ISpoolmanService implementation
        _ = services.AddHttpClient<ISpoolmanService, SpoolmanService>("SpoolmanService", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Authentication
        _ = services.AddScoped<IPasswordHashingService, PasswordHashingService>();
        _ = services.AddScoped<IAuthenticationService, AuthenticationService>();

        // Startup tracking
        // Register StartupStatus as the implementation for IStartupStatus
        _ = services.AddSingleton<IStartupStatus, StartupStatus>();

        // Harvest queue and gcode harvest service
        // IHarvestQueue must be Singleton because it's used by background tasks that outlive HTTP request scopes
        _ = services.AddSingleton<IHarvestQueue, InMemoryHarvestQueue>();
        _ = services.AddScoped<IGcodeHarvestService, GcodeHarvestService>();

        // Background worker to process harvest file jobs from the queue
        _ = services.AddHostedService<HarvestWorkerService>();

        // Realtime update service for Klipper/Moonraker printers
        _ = services.AddHostedService<MoonrakerSubscriptionService>();
        return services;
    }
}
