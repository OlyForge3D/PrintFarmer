using System;
using System.Diagnostics;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Interceptors;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Discovery;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Quota;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Extensions;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Services.Discovery;
using Farm.Web.Api.Services.Gcode;
using Farm.Web.Api.Services.Startup;
using Farm.Web.Api.Services.StorageManagement;
using Fido2NetLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Farm.Web.Api.Infrastructure;

/// <summary>
/// Consolidated service registration extensions for the PrintFarmer API.
/// All service registrations are organized by functional area.
/// </summary>
public static class ServiceCollectionExtensions
{
    #region Database

    public static IServiceCollection AddPrintFarmerDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(configuration);

        // Register the encryption interceptor as a singleton (it needs ISensitiveDataProtector)
        // Note: We don't use the interceptor in EF Core because it causes DI lifetime issues.
        // Instead, encryption is handled at the service layer in PrintersService.
        _ = services.AddSingleton<SensitiveDataEncryptionInterceptor>();

        // Register telemetry interceptor for automatic database operation metrics
        _ = services.AddSingleton<TelemetrySaveChangesInterceptor>();

        // Register DbContext with scoped lifetime (default)
        _ = services.AddDbContext<AppDbContext>((sp, options) =>
        {
            ConfigureAppDbProvider(options, dbConfig);
            options.AddInterceptors(sp.GetRequiredService<TelemetrySaveChangesInterceptor>());
        });

        // Register a factory that shares the same configuration as AddDbContext.
        // Use Scoped lifetime to match the scoped DbContextOptions registered by AddDbContext.
        _ = services.AddDbContextFactory<AppDbContext>(
            (sp, options) =>
            {
                ConfigureAppDbProvider(options, dbConfig);
                options.AddInterceptors(sp.GetRequiredService<TelemetrySaveChangesInterceptor>());
            },
            ServiceLifetime.Scoped);

        return services;
    }

    /// <summary>
    /// Configures the EF Core provider for <see cref="AppDbContext"/> using the resolved
    /// <paramref name="dbConfig"/>. Uses API-specific migration assembly names.
    /// </summary>
    private static void ConfigureAppDbProvider(DbContextOptionsBuilder options, DatabaseProviderConfiguration dbConfig)
    {
        if (dbConfig.IsSqlServer)
        {
            _ = options.UseSqlServer(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.SqlServer"));
        }
        else if (dbConfig.IsPostgres)
        {
            _ = options.UseNpgsql(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.PostgreSQL"));
        }
        else
        {
            // SQLite: Development only - uses EnsureCreated, no migrations
            _ = options.UseSqlite(dbConfig.ConnectionString);
        }
    }

    #endregion

    #region Settings

    public static IServiceCollection AddPrintFarmerSettings(this IServiceCollection services)
    {
        // Register configuration-bound system settings (no DB access required)
        // Bind DatabaseSettings from configuration section name defined on the type
        try
        {
            // Use the SectionName constant if present
            _ = services.Configure<DatabaseSettings>(s => { });
        }
        catch
        {
        }

        // Register a lightweight provider for system settings that reads from IConfiguration
        _ = services.AddSingleton<ISystemSettingsProvider, ConfigurationSystemSettingsProvider>();

        // SettingsService registration moved to after repositories (requires IAppSettingsRepository)
        // See Repositories section below

        // Settings initialization from environment variables (scoped to match ISettingsService)
        // Register settings initialization via its interface
        _ = services.AddScoped<ISettingsInitializationService, SettingsInitializationService>();

        return services;
    }

    #endregion

    #region Main Services Entry Point

    /// <summary>
    /// Registers all PrintFarmer services. This is the main entry point for service registration.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    public static IServiceCollection AddPrintFarmerServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Check if background services should be disabled (for testing)
        bool disableBackgroundServices = ShouldDisableBackgroundServices();

        // Register services by category
        RegisterCoreInfrastructure(services);
        RegisterRepositories(services);
        RegisterSettingsService(services);  // Must be after RegisterRepositories (depends on IAppSettingsRepository)
        RegisterTelemetryAndLogging(services);
        RegisterCachingServices(services);
        RegisterAuthenticationServices(services);
        RegisterPasskeyServices(services, configuration);
        RegisterEmailServices(services);
        RegisterRateLimitingServices(services);
        RegisterCatalogServices(services);

        // Cost tracking
        _ = services.AddScoped<Farm.Infrastructure.Services.Cost.IFilamentCostProvider, Farm.Infrastructure.Services.Cost.SpoolmanFilamentCostProvider>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Cost.IJobCostCalculationService, Farm.Infrastructure.Services.Cost.JobCostCalculationService>();

        // Statistics services (depends on database)
        _ = services.AddScoped<Farm.Infrastructure.Services.Statistics.IStatisticsService, Farm.Infrastructure.Services.Statistics.StatisticsService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Statistics.IReportExportService, Farm.Infrastructure.Services.Statistics.ReportExportService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Statistics.ICorrelationAnalyticsService, Farm.Infrastructure.Services.Statistics.CorrelationAnalyticsService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Statistics.IPredictiveAnalyticsService, Farm.Infrastructure.Services.Statistics.PredictiveAnalyticsService>();

        // Print job queue services (API-owned, not slicer-module)
        _ = services.AddScoped<Farm.Infrastructure.Services.Queue.IQueueDataService, Farm.Infrastructure.Services.Queue.QueueDataService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Queue.IJobQueueService, Farm.Infrastructure.Services.Queue.JobQueueService>();

        // Dispatch scoring engine and service
        _ = services.AddScoped<Farm.Infrastructure.Services.Queue.Dispatch.IDispatchScorer, Farm.Infrastructure.Services.Queue.Dispatch.DispatchScorer>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Queue.Dispatch.IJobDispatchService, Farm.Infrastructure.Services.Queue.Dispatch.JobDispatchService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Queue.Dispatch.IBatchDispatchService, Farm.Infrastructure.Services.Queue.Dispatch.BatchDispatchService>();

        // Material equivalence clusters
        _ = services.AddScoped<Farm.Infrastructure.Services.MaterialClusters.IMaterialClusterService, Farm.Infrastructure.Services.MaterialClusters.MaterialClusterService>();

        // Printer group service
        _ = services.AddScoped<Farm.Infrastructure.Services.PrinterGroups.IPrinterGroupService, Farm.Infrastructure.Services.PrinterGroups.PrinterGroupService>();

        // Bed type service
        _ = services.AddScoped<Farm.Infrastructure.Services.BedTypes.IBedTypeService, Farm.Infrastructure.Services.BedTypes.BedTypeService>();

        // Custom field service
        _ = services.AddScoped<Farm.Infrastructure.Services.CustomFields.ICustomFieldService, Farm.Infrastructure.Services.CustomFields.CustomFieldService>();

        // Farm settings service (consolidates farm-wide config access)
        _ = services.AddScoped<Farm.Infrastructure.Services.IFarmSettingsService, Farm.Infrastructure.Services.FarmSettingsService>();

        // Auto-dispatch trigger (singleton event bus between scoped services and background service)
        var autoDispatchTrigger = new Farm.Infrastructure.Services.Queue.Dispatch.AutoDispatchTrigger();
        _ = services.AddSingleton(autoDispatchTrigger);
        _ = services.AddSingleton<Farm.Infrastructure.Services.Queue.Dispatch.IAutoDispatchTrigger>(autoDispatchTrigger);

        _ = services.Configure<Farm.Infrastructure.Settings.BackendTimeoutSettings>(configuration.GetSection(Farm.Infrastructure.Settings.BackendTimeoutSettings.SectionName));
        _ = services.Configure<Farm.Infrastructure.Settings.ObicoSettings>(configuration.GetSection(Farm.Infrastructure.Settings.ObicoSettings.SectionName));
        RegisterBackendClientPlugins(services, configuration);  // Register backend client plugins FIRST - they register HTTP clients
        RegisterHttpClients(services);
        RegisterPrinterServices(services);  // Then register printer services that depend on HTTP clients
        RegisterModelAndGcodeServices(services, configuration, disableBackgroundServices);
        RegisterSetupAndSchemaServices(services);
        RegisterBackgroundServices(services, disableBackgroundServices);
        RegisterSmartPlugProviders(services);

        return services;
    }

    #endregion

    #region Core Infrastructure

    private static void RegisterCoreInfrastructure(IServiceCollection services)
    {
        // Database initialization
        _ = services.AddScoped<DatabaseInitializer>();
        _ = services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();

        // YAML seed data services
        _ = services.AddScoped<IYamlSeedDataReader, YamlSeedDataReader>();
        _ = services.AddScoped<IDataSeedService, DataSeedService>();
        _ = services.AddScoped<IDataExportService, DataExportService>();
        _ = services.AddScoped<IDataImportService, DataImportService>();

        // Catalog update detection service
        _ = services.AddScoped<ICatalogUpdateService, CatalogUpdateService>();
        _ = services.AddHttpClient("CatalogUpdate", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PrintFarmer/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Network URL rewriting (stateless, safe as Singleton)
        _ = services.AddSingleton<INetworkUrlRewriteService, NetworkUrlRewriteService>();

        // Circuit breaker for resilient external calls
        _ = services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();

        // Application path provider abstraction (bridges ASP.NET Core to Infrastructure layer)
        _ = services.AddSingleton<Farm.Infrastructure.Services.StorageManagement.IApplicationPathProvider, AspNetCorePathProvider>();

        // Storage path service for multi-deployment support (Docker and Kubernetes)
        // Now registered from Infrastructure layer with abstracted path provider
        _ = services.AddSingleton<Farm.Infrastructure.Services.StorageManagement.IStoragePathService, Farm.Infrastructure.Services.StorageManagement.StoragePathService>();

        // File Management Services
        _ = services.AddScoped<IFileManagementService, FileManagementService>();
        _ = services.AddScoped<IFileIntegrityService, FileIntegrityService>();
        _ = services.AddScoped<IChunkedUploadService, ChunkedUploadService>();
        _ = services.AddSingleton<Farm.Infrastructure.Services.Gcode.IGcodeThumbnailExtractorService, GcodeThumbnailExtractorService>();

        // File system abstraction (pure wrapper around static File/Directory APIs)
        _ = services.AddSingleton<Farm.Infrastructure.IO.IFileSystem, Farm.Infrastructure.IO.SystemFileSystem>();

        // Startup status tracking
        _ = services.AddSingleton<Farm.Infrastructure.Services.Startup.IStartupStatus, Farm.Infrastructure.Services.Startup.StartupStatus>();

        // Discovery progress cache for real-time updates
        _ = services.AddSingleton<IDiscoveryProgressCache, DiscoveryProgressCache>();

        // Discovery proxy service for streaming discovery with SignalR progress updates
        _ = services.AddScoped<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService, DiscoveryProxyService>();
    }

    #endregion

    #region Repositories

    private static void RegisterRepositories(IServiceCollection services)
    {
        // Core repositories (non-coordinated)
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Catalog.ICatalogRepository, Farm.Infrastructure.Repositories.Catalog.EfCatalogRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Users.IUsersRepository, Farm.Infrastructure.Repositories.Users.EfUsersRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.SystemLogs.ISystemLogRepository, Farm.Infrastructure.Repositories.SystemLogs.EfSystemLogRepository>();

        // Unit of Work pattern: coordinates access to 6 repositories with a shared DbContext
        // This prevents FK constraint violations and ensures atomic transactions across coordinated operations:
        // - Gcode + Harvest: Harvest operations reference gcode files
        // - Gcode + Folders: Gcode files organized in folder hierarchy
        // - Harvest + Printers: Harvest operations tied to specific printers
        // - Model3dFiles + Folders: 3D models organized in folder hierarchy
        // - Locations + Printers: Printers located at specific facilities
        _ = services.AddScoped<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork, Farm.Infrastructure.Repositories.UnitOfWork.AppUnitOfWork>();

        // Individual repository registrations (for backward compatibility with existing code)
        // These are resolved through IUnitOfWork for coordinated operations
        _ = services.AddScoped(sp => sp.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>().GcodeFiles);
        _ = services.AddScoped(sp => sp.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>().HarvestOperations);
        _ = services.AddScoped(sp => sp.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>().Printers);
        _ = services.AddScoped(sp => sp.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>().Folders);
        _ = services.AddScoped(sp => sp.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>().Locations);

        // Tag repositories
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Tags.ITagRepository, Farm.Infrastructure.Repositories.Tags.EfTagRepository>();

        // Queue repositories
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Queue.IQueueRepository, Farm.Infrastructure.Repositories.Queue.EfQueueRepository>();

        // Print approval repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.PrintJobs.IPrintApprovalRepository, Farm.Infrastructure.Repositories.PrintJobs.EfPrintApprovalRepository>();

        // Filament repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Filament.IFilamentTypeRepository, Farm.Infrastructure.Repositories.Filament.FilamentTypeRepository>();

        // Password policy repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.PasswordPolicy.IPasswordPolicyRepository, Farm.Infrastructure.Repositories.PasswordPolicy.PasswordPolicyRepository>();

        // Schema health repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.SchemaHealth.ISchemaHealthRepository, Farm.Infrastructure.Repositories.SchemaHealth.SchemaHealthRepository>();

        // Slicer repositories are registered by AddSlicerModule() in Farm.Slicer.Module

        // Task repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Tasks.IUserTaskRepository, Farm.Infrastructure.Repositories.Tasks.EfUserTaskRepository>();

        // Settings repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Settings.IAppSettingsRepository, Farm.Infrastructure.Repositories.Settings.EfAppSettingsRepository>();

        // Gcode repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Gcode.IGcodeRepository, Farm.Infrastructure.Repositories.Gcode.EfGcodeRepository>();

        // Authentication audit repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Authentication.IAuthAuditLogRepository, Farm.Infrastructure.Repositories.Authentication.EfAuthAuditLogRepository>();

        // Webhook repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Webhooks.IWebhookRepository, Farm.Infrastructure.Repositories.Webhooks.EfWebhookRepository>();

        // Printer group repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.PrinterGroups.IPrinterGroupRepository, Farm.Infrastructure.Repositories.PrinterGroups.EfPrinterGroupRepository>();
    }

    #endregion

    #region Settings Service (Depends on Repositories)

    private static void RegisterSettingsService(IServiceCollection services)
    {
        // Register SettingsService AFTER repositories - requires IAppSettingsRepository
        _ = services.AddScoped<ISettingsService, SettingsService>();
    }

    #endregion

    #region Telemetry and Logging

    private static void RegisterTelemetryAndLogging(IServiceCollection services)
    {
        ActivitySource activitySource = new("PrintFarmer.API");
        _ = services.AddSingleton(_ => activitySource);

        // Telemetry service (thread-safe, manages Meter/ActivitySource lifetimes)
        _ = services.AddSingleton<IPrintFarmerTelemetryService, PrintFarmerTelemetryService>();

        // Normalization event logger
        _ = services.AddScoped<Farm.Infrastructure.Normalization.INormalizationEventLogger, Farm.Infrastructure.Normalization.NormalizationEventLogger>();
    }

    #endregion

    #region Caching

    private static void RegisterCachingServices(IServiceCollection services)
    {
        _ = services.AddMemoryCache();
        _ = services.AddDistributedMemoryCache();
        _ = services.AddOptions<Farm.Infrastructure.Services.Catalog.Caching.CatalogCacheOptions>();
        _ = services.AddOptions<Farm.Infrastructure.Services.Printers.PrinterVersionCacheOptions>();

        // CatalogCache resolves scoped AppDbContext per-call, so it can be a Singleton
        _ = services.AddSingleton<Farm.Infrastructure.Services.Catalog.Caching.ICatalogCacheProvider, Farm.Infrastructure.Services.Catalog.Caching.CatalogCache>();

        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterVersionCache, Farm.Infrastructure.Services.Printers.PrinterVersionCache>();

        // Migration status for health checks
        _ = services.AddScoped<Farm.Infrastructure.Data.IMigrationStatusProvider, Farm.Infrastructure.Data.MigrationStatusProvider>();
    }

    #endregion

    #region Authentication and Security

    private static void RegisterAuthenticationServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.IPasswordHashingService, Farm.Infrastructure.Services.Authentication.PasswordHashingService>();
        _ = services.AddScoped<IAuthenticationService, Farm.Infrastructure.Services.Authentication.AuthenticationService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.PasswordPolicy.IPasswordPolicyService, Farm.Infrastructure.Services.PasswordPolicy.PasswordPolicyService>();
        _ = services.AddScoped<IAccountLockoutService, Farm.Infrastructure.Services.Authentication.AccountLockoutService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.IAuthAuditService, Farm.Infrastructure.Services.Authentication.AuthAuditService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.ILoginAuditService, Farm.Infrastructure.Services.Authentication.LoginAuditService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.ITokenRevocationService, Farm.Infrastructure.Services.Authentication.TokenRevocationService>();
        _ = services.AddHostedService<Services.Authentication.TokenRevocationCleanupService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Users.IUsersService, Farm.Infrastructure.Services.Users.UsersService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.IPasskeyService, Farm.Infrastructure.Services.Authentication.PasskeyService>();
    }

    private static void RegisterPasskeyServices(IServiceCollection services, IConfiguration configuration)
    {
        Fido2Configuration fido2Config = new()
        {
            ServerDomain = configuration["WebAuthn:RelyingPartyId"] ?? "localhost",
            ServerName = configuration["WebAuthn:RelyingPartyName"] ?? "PrintFarmer",
            Origins = new HashSet<string> { configuration["WebAuthn:Origin"] ?? "http://localhost:3000" },
            TimestampDriftTolerance = 300_000,
        };

        _ = services.AddSingleton(new Fido2(fido2Config));
    }

    #endregion

    #region Email

    private static void RegisterEmailServices(IServiceCollection services)
    {
        _ = services.AddSingleton<IEmailTemplateRenderer, EmailTemplateRenderer>();
        _ = services.AddSingleton(sp =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            EmailOptions opts = new EmailOptions();
            cfg.GetSection("Email").Bind(opts);
            return opts;
        });

        // Register HttpClient for Mailjet
        _ = services.AddHttpClient("Mailjet", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        _ = services.AddScoped<IEmailService>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            EmailOptions opts = sp.GetRequiredService<EmailOptions>();
            IEmailTemplateRenderer renderer = sp.GetRequiredService<IEmailTemplateRenderer>();
            IHttpClientFactory httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            return opts.Mailjet?.ApiKey != null
                ? new MailjetEmailService(loggerFactory.CreateLogger<MailjetEmailService>(), opts, renderer, httpClientFactory)
                : new ConsoleEmailService(loggerFactory.CreateLogger<ConsoleEmailService>());
        });
    }

    #endregion

    #region Rate Limiting

    private static void RegisterRateLimitingServices(IServiceCollection services)
    {
        _ = services.AddSingleton(sp =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            RateLimitOptions opts = new RateLimitOptions();
            cfg.GetSection("RateLimiting").Bind(opts);
            return opts;
        });
        _ = services.AddSingleton<Farm.Infrastructure.Services.RateLimiting.IRateLimitService, Farm.Infrastructure.Services.RateLimiting.InMemoryRateLimitService>();
    }

    #endregion

    #region Catalog

    private static void RegisterCatalogServices(IServiceCollection services)
    {
        // CatalogCache now directly implements ICatalogCacheProvider — no adapter needed

        // Register Infrastructure catalog service with cache abstraction
        _ = services.AddScoped<Farm.Infrastructure.Services.Catalog.ICatalogService, Farm.Infrastructure.Services.Catalog.CatalogService>();

        // Register API adapter that wraps Infrastructure service to work with request DTOs
        _ = services.AddScoped<Services.Catalog.ICatalogService, Services.Catalog.CatalogServiceAdapter>();

        _ = services.AddScoped<Farm.Infrastructure.Services.Filament.IFilamentTypeService, Farm.Infrastructure.Services.Filament.FilamentTypeService>();

        // SpoolmanDB community database service (GitHub Pages primary for temp ranges, Spoolman external fallback)
        _ = services.AddHttpClient<Farm.Infrastructure.Services.Spoolman.ISpoolmanDbService, Farm.Infrastructure.Services.Spoolman.SpoolmanDbService>();

        // Open Filament Database community service (static JSON on GitHub Pages)
        _ = services.AddHttpClient<Farm.Infrastructure.Services.OpenFilamentDb.IOpenFilamentDbService, Farm.Infrastructure.Services.OpenFilamentDb.OpenFilamentDbService>();
    }

    #endregion

    #region Printer Services

    private static void RegisterPrinterServices(IServiceCollection services)
    {
        // Register the backend client factory for unified access to all backend clients
        // The factory dynamically discovers clients from plugins via IBackendPluginRegistry
        // This eliminates the need to pass individual backend clients (Moon, Prusa, SDCP, OctoPrint)
        // to PrintersService, making it easier to add new backends without modifying the constructor
        // IMPORTANT: Must be SCOPED because backend clients (e.g., IMoonrakerClient) are scoped services
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IBackendClientFactory>(provider =>
        {
            IServiceProvider serviceProvider = provider;
            IBackendPluginRegistry pluginRegistry = provider.GetRequiredService<Farm.Backend.Plugin.Core.IBackendPluginRegistry>();
            ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();

            return new Farm.Infrastructure.Services.Printers.BackendClientFactory(serviceProvider, pluginRegistry, loggerFactory.CreateLogger<Farm.Infrastructure.Services.Printers.BackendClientFactory>());
        });

        // Register the backend capability factory for capability-aware client retrieval
        // This factory now integrates with the plugin registry for backend metadata
        // while maintaining backward compatibility with reflection-based detection
        // IMPORTANT: Must be SCOPED because it depends on IBackendClientFactory which is scoped
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IBackendCapabilityFactory>(provider =>
        {
            IBackendClientFactory clientFactory = provider.GetRequiredService<Farm.Infrastructure.Services.Printers.IBackendClientFactory>();
            ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            IBackendPluginRegistry? pluginRegistry = provider.GetService<Farm.Backend.Plugin.Core.IBackendPluginRegistry>();

            return new Farm.Infrastructure.Services.Printers.BackendCapabilityFactory(clientFactory, loggerFactory.CreateLogger<Farm.Infrastructure.Services.Printers.BackendCapabilityFactory>(), pluginRegistry);
        });

        // Register the factory for getting printer status clients from plugins
        _ = services.AddSingleton<Farm.Infrastructure.Services.Printers.IPrinterStatusClientFactory>(provider =>
        {
            IServiceProvider serviceProvider = provider;
            IBackendPluginRegistry pluginRegistry = provider.GetRequiredService<Farm.Backend.Plugin.Core.IBackendPluginRegistry>();
            ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();

            return new Farm.Infrastructure.Services.Printers.PrinterStatusClientFactory(serviceProvider, pluginRegistry, loggerFactory.CreateLogger<Farm.Infrastructure.Services.Printers.PrinterStatusClientFactory>());
        });

        // Register the managed spool provider helper for non-Moonraker backends
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.ManagedSpoolProviderHelper>();

        // Register the printer status cache (singleton in Infrastructure - shared across all layers)
        _ = services.AddSingleton<Farm.Infrastructure.Services.Printers.PrinterStatusCache>();
        _ = services.AddSingleton<Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader>(sp => sp.GetRequiredService<Farm.Infrastructure.Services.Printers.PrinterStatusCache>());
        _ = services.AddSingleton<Farm.Infrastructure.Services.Printers.IPrinterStatusCacheWriter>(sp => sp.GetRequiredService<Farm.Infrastructure.Services.Printers.PrinterStatusCache>());

        // Register runtime diagnostic channel service (singleton - toggleable verbose logging per subsystem)
        _ = services.AddSingleton<Farm.Infrastructure.Services.Diagnostics.IDiagnosticChannelService, Farm.Infrastructure.Services.Diagnostics.DiagnosticChannelService>();

        // Register the printer status update receiver (scoped - one per request)
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterStatusUpdateReceiver, Farm.Infrastructure.Services.Printers.PrinterStatusUpdateReceiver>();

        // Register the printer status fallback service for timeout and circuit breaker management
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterStatusFallbackService, Farm.Infrastructure.Services.Printers.PrinterStatusFallbackService>();

        // Register the backend capabilities service for exposing plugin capabilities to the UI
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterBackendCapabilitiesService, Farm.Infrastructure.Services.Printers.PrinterBackendCapabilitiesService>();

        // Register the multi-printer status coordinator for parallel operation orchestration
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IMultiPrinterStatusCoordinator, Farm.Infrastructure.Services.Printers.MultiPrinterStatusCoordinator>();

        // Register SignalR printer status broadcaster - abstracts real-time broadcasting for any UI implementation
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterStatusBroadcaster, Services.Printers.SignalRPrinterStatusBroadcaster>();

        // Register LocationService from Infrastructure layer - location management service
        _ = services.AddScoped<Farm.Infrastructure.Services.Locations.ILocationService, Farm.Infrastructure.Services.Locations.LocationService>();

        // Register CameraService from Infrastructure layer - standalone camera management service
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Cameras.ICameraRepository, Farm.Infrastructure.Repositories.Cameras.EfCameraRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Cameras.ICameraService, Farm.Infrastructure.Services.Cameras.CameraService>();
        _ = services.AddScoped<Farm.Infrastructure.Discovery.IPrinterCameraEndpointDetectionService, Farm.Infrastructure.Discovery.PrinterCameraEndpointDetectionService>();

        // Register go2rtc service - RTSP-to-WebRTC/HLS/MSE transcoding integration
        _ = services.AddScoped<Farm.Infrastructure.Services.Cameras.IGo2RtcService, Farm.Infrastructure.Services.Cameras.Go2RtcService>();

        // Register camera snapshot service - captures snapshots on print events
        _ = services.AddScoped<Farm.Infrastructure.Services.Cameras.ICameraSnapshotService, Farm.Infrastructure.Services.Cameras.CameraSnapshotService>();
        _ = services.AddHttpClient("CameraSnapshot", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Register Obico failure detection service - AI-powered print failure detection
        _ = services.AddScoped<Farm.Infrastructure.Services.FailureDetection.IObicoFailureDetectionService, Farm.Infrastructure.Services.FailureDetection.ObicoFailureDetectionService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.FailureDetection.IFailureDetectionIncidentHistoryService, Farm.Infrastructure.Services.FailureDetection.FailureDetectionIncidentHistoryService>();
        _ = services.AddSingleton<Farm.Infrastructure.Services.FailureDetection.IFailureDetectionMonitorStatus, Farm.Infrastructure.Services.FailureDetection.FailureDetectionMonitorStatusStore>();
        _ = services.AddSingleton<Farm.Infrastructure.Services.FailureDetection.FailureDetectionMetrics>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterSessionTimelineService, Farm.Infrastructure.Services.Printers.PrinterSessionTimelineService>();

        // Register Obico server assignment service - auto-assigns printers to healthy servers
        _ = services.AddScoped<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService, Farm.Infrastructure.Services.FailureDetection.ObicoServerAssignmentService>();

        // Register NfcDeviceService from Infrastructure layer - NFC reader device management
        _ = services.AddScoped<Farm.Infrastructure.Services.NfcDevices.INfcDeviceService, Farm.Infrastructure.Services.NfcDevices.NfcDeviceService>();

        // Register PrintersService from Infrastructure layer - core business logic for any UI implementation
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrintersService, Farm.Infrastructure.Services.Printers.PrintersService>();
    }

    #endregion

    #region Model and Gcode Services

    private static void RegisterModelAndGcodeServices(IServiceCollection services, IConfiguration configuration, bool disableBackgroundServices)
    {
        // Tag services
        _ = services.AddScoped<Farm.Infrastructure.Services.Tags.ITagService, Farm.Infrastructure.Services.Tags.TagService>();

        // Task services (user task management)
        _ = services.AddScoped<Farm.Infrastructure.Services.Tasks.ITaskBroadcaster, Services.Tasks.SignalRTaskBroadcaster>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Tasks.IUserTaskService, Farm.Infrastructure.Services.Tasks.UserTaskService>();

        // SystemLogs service
        _ = services.AddScoped<Farm.Infrastructure.Services.SystemLogs.ISystemLogService, Farm.Infrastructure.Services.SystemLogs.SystemLogService>();

        // Folder management service (shared by model and gcode file services)
        _ = services.AddScoped<IFolderManagementService, FolderManagementService>();

        // Stored file operations service (consolidated file and thumbnail operations)
        _ = services.AddScoped<IStoredFileOperationsService, StoredFileOperationsService>();

        // Model services
        _ = services.AddSingleton<IModelAnalysisService, ModelAnalysisService>();
        _ = services.AddSingleton<IVirusScanner, ClamAVVirusScanner>();
        _ = services.AddSingleton<IThumbnailGenerationService, Farm.Slicer.Module.Services.Rendering.ThumbnailGenerationService>();

        // Harvest configuration and services
        _ = services.Configure<GcodeHarvestSettings>(configuration.GetSection(Farm.Infrastructure.Settings.GcodeHarvestSettings.SectionKey));

        _ = services.AddSingleton<IGcodeMetadataExtractorService, GcodeMetadataExtractorService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Gcode.IPrinterModelAliasService, Farm.Infrastructure.Services.Gcode.PrinterModelAliasService>();
        _ = services.AddScoped<Services.Gcode.IGcodeFilesService, Services.Gcode.GcodeFilesService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Gcode.IGcodeFileProcessingService>(sp =>
            (Farm.Infrastructure.Services.Gcode.IGcodeFileProcessingService)sp.GetRequiredService<Services.Gcode.IGcodeFilesService>());
        _ = services.AddScoped<Farm.Infrastructure.Services.Gcode.IHarvestEventBroadcaster, Services.Gcode.SignalRHarvestEventBroadcaster>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Gcode.IGcodeHarvestService, Farm.Infrastructure.Services.Gcode.GcodeHarvestService>();

        // Gcode harvest queue (async processing)
        _ = services.AddScoped<Farm.Infrastructure.Services.GcodeHarvest.IGcodeHarvestQueue, Farm.Infrastructure.Services.GcodeHarvest.EfGcodeHarvestQueue>();
        if (!disableBackgroundServices)
        {
            _ = services.AddHostedService<Farm.Infrastructure.Services.GcodeHarvest.GcodeHarvestQueueProcessorService>();
        }

        // Gcode upload settings and quota - use persisted settings from ISettingsService
        _ = services.AddScoped<IGcodeUploadSettings, PersistedGcodeUploadSettingsAdapter>();
        _ = services.AddScoped<IGcodeUploadQuotaService, InMemoryGcodeUploadQuotaService>();

        // Print quotas and user balances
        _ = services.AddScoped<Farm.Infrastructure.Services.PrintQuotas.IPrintQuotaService, Farm.Infrastructure.Services.PrintQuotas.PrintQuotaService>();
    }

    #endregion

    #region Setup and Schema

    private static void RegisterSetupAndSchemaServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Infrastructure.Services.Setup.ISetupService, Farm.Infrastructure.Services.Setup.SetupService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.SchemaHealth.ISchemaHealthService, Farm.Infrastructure.Services.SchemaHealth.SchemaHealthService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.SignalR.ISignalRTestService, Services.SignalR.SignalRTestService>();
    }

    private static void RegisterBackendClientPlugins(IServiceCollection services, IConfiguration configuration)
    {
        // Discover and register all backend client plugins.
        // BackendPlugins:PluginsPath (appsettings.json) is scanned for runtime-loaded plugin DLLs
        // in addition to the main app output directory.
        services.AddBackendClientPlugins(configuration);
    }

    #endregion

    #region HTTP Clients

    private static void RegisterHttpClients(IServiceCollection services)
    {
        // Backend-specific HTTP clients are now registered by their respective plugins
        // via the IExtendedBackendPlugin.RegisterAdditionalServices() method:
        // - Moonraker HTTP client (10s timeout)
        // - PrusaLink HTTP client (10s timeout)
        // - OctoPrint HTTP client (10s timeout)
        // - SDCP HTTP client (10s timeout)
        // This keeps backend-specific HTTP client configuration encapsulated in plugins

        // Spoolman Integration (not backend-specific, registered centrally)
        _ = services.AddHttpClient<ISpoolmanService, Farm.Infrastructure.Services.Spoolman.SpoolmanService>("SpoolmanService", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Obico ML API HTTP client (15s timeout for image analysis)
        _ = services.AddHttpClient("ObicoML", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // Smart plug HTTP client shared by Tasmota, Shelly, and HomeAssistant providers (5s timeout for LAN devices)
        _ = services.AddHttpClient("SmartPlug", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });
    }

    #endregion

    #region Background Services

    private static void RegisterBackgroundServices(IServiceCollection services, bool disableBackgroundServices)
    {
        // Background service monitor - always register as it's used for status reporting
        _ = services.AddSingleton<Farm.Infrastructure.Services.Background.IBackgroundServiceMonitor, Farm.Infrastructure.Services.Background.BackgroundServiceMonitor>();

        if (!disableBackgroundServices)
        {
            // System log cleanup (common service, not plugin-specific)
            _ = services.AddHostedService<Farm.Infrastructure.Services.SystemLogs.SystemLogCleanupService>();

            // Discovery heartbeat monitor - tracks external discovery microservice status
            _ = services.AddSingleton<Farm.Web.Api.Services.Workers.DiscoveryHeartbeatMonitorService>();
            _ = services.AddHostedService(sp => sp.GetRequiredService<Farm.Web.Api.Services.Workers.DiscoveryHeartbeatMonitorService>());

            // Auto-dispatch background service (event-driven, reacts to printer-idle triggers)
            _ = services.AddHostedService<Farm.Infrastructure.Services.Queue.Dispatch.AutoDispatchBackgroundService>();

            // Camera health monitor - periodic HTTP probes of camera snapshot URLs
            _ = services.AddHostedService<Farm.Infrastructure.Services.Cameras.CameraHealthMonitorService>();

            // Print failure monitor - AI-powered failure detection using Obico ML API
            _ = services.AddHostedService<Farm.Infrastructure.Services.FailureDetection.PrintFailureMonitorService>();

            // Slicer hosted services (WorkerHealthMonitor, JobDispatching,
            // JobTimeoutScanner, StaleWorkerCleanup) are now registered by
            // AddSlicerModule() in Farm.Slicer.Module.

            // Backend-specific background services are now registered by their respective plugins
            // via the IExtendedBackendPlugin.RegisterAdditionalServices() method:
            // - MoonrakerSubscriptionService (real-time WebSocket subscriptions)
            // - PrusaLinkPollingService (HTTP polling every 5 seconds)
            // - OctoPrintPollingService (HTTP polling every 10 seconds)
            // This keeps backend-specific logic encapsulated in plugins
        }
    }

    #endregion

    #region Smart Plug Providers

    private static void RegisterSmartPlugProviders(IServiceCollection services)
    {
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.KasaSmartPlugProvider>();
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.TasmotaSmartPlugProvider>();
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.ShellySmartPlugProvider>();
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.HomeAssistantSmartPlugProvider>();
    }

    #endregion

    #region Helpers

    private static bool ShouldDisableBackgroundServices()
    {
        try
        {
            string? env = Environment.GetEnvironmentVariable("TEST_DISABLE_BACKGROUND_SERVICES");
            return !string.IsNullOrEmpty(env) && (string.Equals(env, "true", StringComparison.OrdinalIgnoreCase) || env == "1");
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
