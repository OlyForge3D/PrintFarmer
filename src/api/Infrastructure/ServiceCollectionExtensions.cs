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
        // Build a DbContextOptions<AppDbContext> instance configured for the selected provider and
        // register it as a Singleton so the factory and other singletons can consume it safely.
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            _ = optionsBuilder.UseSqlServer(connectionString);
        }
        else if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
        {
            _ = optionsBuilder.UseNpgsql(connectionString);
        }
        else if (provider.Equals("mysql", StringComparison.OrdinalIgnoreCase))
        {
            _ = optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
        }
        else
        {
            _ = optionsBuilder.UseSqlite(connectionString);
        }

        _ = services.AddSingleton<DbContextOptions<AppDbContext>>(optionsBuilder.Options);
        _ = services.AddDbContextFactory<AppDbContext>();

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

    public static IServiceCollection AddPrintFarmerServices(this IServiceCollection services, IConfiguration configuration)
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

        // Repositories (in infra project)
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Printers.IPrintersRepository, Farm.Infrastructure.Repositories.Printers.EfPrintersRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Slicing.IProfilesRepository, Farm.Infrastructure.Repositories.Slicing.EfProfilesRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Queue.IQueueRepository, Farm.Infrastructure.Repositories.Queue.EfQueueRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.SystemLogs.ISystemLogRepository, Farm.Infrastructure.Repositories.SystemLogs.EfSystemLogRepository>();
        // Catalog repository contract moved to infra; register infra implementation
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Catalog.ICatalogRepository, Farm.Infrastructure.Repositories.Catalog.EfCatalogRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Users.IUsersRepository, Farm.Infrastructure.Repositories.Users.EfUsersRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.PrinterCapabilities.IPrinterCapabilitiesRepository, Farm.Infrastructure.Repositories.PrinterCapabilities.EfPrinterCapabilitiesRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Harvest.IHarvestRepository, Farm.Infrastructure.Repositories.Harvest.EfHarvestRepository>();

        // Business Services
        _ = services.AddScoped<IDefaultCatalogService, DefaultCatalogService>();
        _ = services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();
        // SystemLogCleanupService is a background worker; register as hosted service
        // Skip background hosted services during tests when TEST_DISABLE_BACKGROUND_SERVICES is set.
        bool disableBg = false;
        try
        {
            var env = Environment.GetEnvironmentVariable("TEST_DISABLE_BACKGROUND_SERVICES");
            disableBg = !string.IsNullOrEmpty(env) && (string.Equals(env, "true", StringComparison.OrdinalIgnoreCase) || env == "1");
        }
        catch { }

        if (!disableBg)
        {
            _ = services.AddHostedService<SystemLogCleanupService>();
        }
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

        // Storage path service for multi-deployment support (Docker and Kubernetes)
        // Provides centralized configuration for file storage paths
        _ = services.AddSingleton<Farm.Web.Api.Services.StorageManagement.IStoragePathService, Farm.Web.Api.Services.StorageManagement.StoragePathService>();

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

        // Email (MVP)
        services.AddSingleton<Farm.Web.Api.Services.Email.IEmailTemplateRenderer, Farm.Web.Api.Services.Email.EmailTemplateRenderer>();
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = new Farm.Web.Api.Services.Email.EmailOptions();
            cfg.GetSection("Email").Bind(opts);
            return opts;
        });
        services.AddScoped<Farm.Web.Api.Services.Email.IEmailService>(sp =>
        {
            var logger = sp.GetRequiredService<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var opts = sp.GetRequiredService<Farm.Web.Api.Services.Email.EmailOptions>();
            var renderer = sp.GetRequiredService<Farm.Web.Api.Services.Email.IEmailTemplateRenderer>();
            return opts.Provider?.Equals("mailjet", StringComparison.OrdinalIgnoreCase) == true
                ? new Farm.Web.Api.Services.Email.MailjetEmailService(logger, opts, renderer)
                : new Farm.Web.Api.Services.Email.ConsoleEmailService(logger, renderer);
        });

        // Rate Limiting
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = new Farm.Web.Api.Services.RateLimiting.RateLimitOptions();
            cfg.GetSection("RateLimiting").Bind(opts);
            return opts;
        });
        services.AddSingleton<Farm.Web.Api.Services.RateLimiting.IRateLimitService, Farm.Web.Api.Services.RateLimiting.InMemoryRateLimitService>();

        // Job dispatch retry options
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = new Farm.Web.Api.Services.JobDispatch.RetryOptions();
            cfg.GetSection("JobDispatchRetry").Bind(opts);
            return opts;
        });

        // Startup tracking
        // Register StartupStatus as the implementation for IStartupStatus
        _ = services.AddSingleton<IStartupStatus, StartupStatus>();

        // Harvest configuration
        _ = services.Configure<Farm.Infrastructure.Settings.GcodeHarvestSettings>(configuration.GetSection(Farm.Infrastructure.Settings.GcodeHarvestSettings.SectionKey));

        // Harvest queue and gcode harvest service
        // IHarvestQueue must be Singleton because it's used by background tasks that outlive HTTP request scopes
        _ = services.AddSingleton<IHarvestQueue, InMemoryHarvestQueue>();
        _ = services.AddScoped<IGcodeHarvestService, GcodeHarvestService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Gcode.IGcodeMetadataExtractorService, Farm.Web.Api.Services.Gcode.GcodeMetadataExtractorService>();

        // Background worker to process harvest file jobs from the queue
        if (!disableBg)
        {
            _ = services.AddHostedService<HarvestWorkerService>();

            // Realtime update service for Klipper/Moonraker printers
            _ = services.AddHostedService<MoonrakerSubscriptionService>();
        }
        return services;
    }
}
