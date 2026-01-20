using System;
using System.Diagnostics;
using AutoMapper;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Slicing.Libraries;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Extensions;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.JobDispatch;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Api.Services.Slicing.Abstractions;
using Farm.Web.Api.Services.StorageManagement;
using Farm.Web.Api.Services.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
            else
            {
                _ = options.UseSqlite(connectionString);
            }
        });

        // Also register a DbContextFactory for creating short-lived AppDbContext instances from singletons
        // Build a DbContextOptions<AppDbContext> instance configured for the selected provider and
        // register it as a Singleton so the factory and other singletons can consume it safely.
        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            _ = optionsBuilder.UseSqlServer(connectionString);
        }
        else if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
        {
            _ = optionsBuilder.UseNpgsql(connectionString);
        }
        else
        {
            _ = optionsBuilder.UseSqlite(connectionString);
        }

        _ = services.AddSingleton(optionsBuilder.Options);
        _ = services.AddDbContextFactory<AppDbContext>();

        return services;
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
        // AutoMapper
        _ = services.AddAutoMapper(typeof(Program).Assembly);

        // Check if background services should be disabled (for testing)
        bool disableBackgroundServices = ShouldDisableBackgroundServices();

        // Register services by category
        RegisterCoreInfrastructure(services);
        RegisterRepositories(services);
        RegisterSettingsService(services);  // Must be after RegisterRepositories (depends on IAppSettingsRepository)
        RegisterTelemetryAndLogging(services);
        RegisterCachingServices(services);
        RegisterAuthenticationServices(services);
        RegisterEmailServices(services);
        RegisterRateLimitingServices(services);
        RegisterImportingServices(services);
        RegisterCatalogServices(services);
        RegisterSlicingServices(services, configuration);
        RegisterBackendClientPlugins(services);  // Register backend client plugins FIRST - they register HTTP clients
        RegisterHttpClients(services);
        RegisterPrinterServices(services);  // Then register printer services that depend on HTTP clients
        RegisterModelAndGcodeServices(services, configuration, disableBackgroundServices);
        RegisterArtifactServices(services, configuration, disableBackgroundServices);
        RegisterSetupAndSchemaServices(services);
        RegisterBackgroundServices(services, disableBackgroundServices);

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
        _ = services.AddScoped<Services.FileManagement.IFileManagementService, Services.FileManagement.FileManagementService>();
        _ = services.AddScoped<Services.FileManagement.IFileIntegrityService, Services.FileManagement.FileIntegrityService>();
        _ = services.AddScoped<Services.FileManagement.IChunkedUploadService, Services.FileManagement.ChunkedUploadService>();
        _ = services.AddSingleton<Farm.Infrastructure.Services.Gcode.IGcodeThumbnailExtractorService, Services.FileManagement.GcodeThumbnailExtractorService>();

        // File system abstraction (pure wrapper around static File/Directory APIs)
        _ = services.AddSingleton<Services.IO.IFileSystem, Services.IO.SystemFileSystem>();

        // Startup status tracking
        _ = services.AddSingleton<IStartupStatus, StartupStatus>();

        // Discovery progress cache for real-time updates
        _ = services.AddSingleton<IDiscoveryProgressCache, DiscoveryProgressCache>();

        // Discovery proxy service for streaming discovery with SignalR progress updates
        _ = services.AddScoped<Services.Interfaces.IDiscoveryProxyService, Services.DiscoveryProxyService>();
    }

    #endregion

    #region Repositories

    private static void RegisterRepositories(IServiceCollection services)
    {
        // Core repositories (non-coordinated)
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Catalog.ICatalogRepository, Farm.Infrastructure.Repositories.Catalog.EfCatalogRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Users.IUsersRepository, Farm.Infrastructure.Repositories.Users.EfUsersRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.SystemLogs.ISystemLogRepository, Farm.Infrastructure.Repositories.SystemLogs.EfSystemLogRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.FileConsistency.IFileConsistencyRepository, Farm.Infrastructure.Repositories.FileConsistency.EfFileConsistencyRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.FileConsistency.IFileAuditRepository, Farm.Infrastructure.Repositories.FileConsistency.EfFileAuditRepository>();

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
        _ = services.AddScoped(sp => sp.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>().Model3dFiles);
        _ = services.AddScoped(sp => sp.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>().Locations);

        // Tag repositories
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Tags.ITagRepository, Farm.Infrastructure.Repositories.Tags.EfTagRepository>();

        // Queue repositories
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Queue.IQueueRepository, Farm.Infrastructure.Repositories.Queue.EfQueueRepository>();

        // New PrintJobQueue adapter service (DB-backed via existing JobQueueService)
        _ = services.AddScoped<Farm.Web.Api.Services.PrintJobQueue.IPrintJobQueueService, Farm.Web.Api.Services.PrintJobQueue.PrintJobQueueAdapter>();

        // Filament repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Filament.IFilamentTypeRepository, Farm.Infrastructure.Repositories.Filament.FilamentTypeRepository>();

        // Artifacts repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Artifacts.IArtifactsRepository, Farm.Infrastructure.Repositories.Artifacts.EfArtifactsRepository>();

        // Password policy repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.PasswordPolicy.IPasswordPolicyRepository, Farm.Infrastructure.Repositories.PasswordPolicy.PasswordPolicyRepository>();

        // Schema health repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.SchemaHealth.ISchemaHealthRepository, Farm.Infrastructure.Repositories.SchemaHealth.SchemaHealthRepository>();

        // Slicing repositories
        _ = services.AddScoped<IProfilesRepository, EfProfilesRepository>();
        _ = services.AddScoped<IProcessProfileRepository, EfProcessProfileRepository>();
        _ = services.AddScoped<IMachineProfileRepository, EfMachineProfileRepository>();
        _ = services.AddScoped<IFilamentProfileRepository, EfFilamentProfileRepository>();
        _ = services.AddScoped<ISlicersRepository, EfSlicersRepository>();
        _ = services.AddScoped<ISliceJobRepository, EfSliceJobRepository>();

        // Worker repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Workers.IWorkerRepository, Farm.Infrastructure.Repositories.Workers.EfWorkerRepository>();

        // Settings repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Settings.IAppSettingsRepository, Farm.Infrastructure.Repositories.Settings.EfAppSettingsRepository>();

        // Gcode repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Gcode.IGcodeRepository, Farm.Infrastructure.Repositories.Gcode.EfGcodeRepository>();

        // Authentication audit repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Authentication.IAuthAuditLogRepository, Farm.Infrastructure.Repositories.Authentication.EfAuthAuditLogRepository>();
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

        // Unified logging service (Singleton because used by Singleton services like IHarvestQueue)
        _ = services.AddSingleton<IUnifiedLoggingService, UnifiedLoggingService>();

        // Normalization event logger
        _ = services.AddScoped<Farm.Infrastructure.Normalization.INormalizationEventLogger, Farm.Infrastructure.Normalization.NormalizationEventLogger>();
    }

    #endregion

    #region Caching

    private static void RegisterCachingServices(IServiceCollection services)
    {
        _ = services.AddMemoryCache();
        _ = services.AddOptions<CatalogCacheOptions>();

        // CatalogCache resolves scoped AppDbContext per-call, so it can be a Singleton
        _ = services.AddSingleton<ICatalogCache, CatalogCache>();
    }

    #endregion

    #region Authentication and Security

    private static void RegisterAuthenticationServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.IPasswordHashingService, Farm.Infrastructure.Services.Authentication.PasswordHashingService>();
        _ = services.AddScoped<IAuthenticationService, Farm.Infrastructure.Services.Authentication.AuthenticationService>();
        _ = services.AddScoped<Services.PasswordPolicy.IPasswordPolicyService, Services.PasswordPolicy.PasswordPolicyService>();
        _ = services.AddScoped<IAccountLockoutService, Farm.Infrastructure.Services.Authentication.AccountLockoutService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.IAuthAuditService, Farm.Infrastructure.Services.Authentication.AuthAuditService>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Authentication.ITokenRevocationService, Farm.Infrastructure.Services.Authentication.TokenRevocationService>();
        _ = services.AddHostedService<Services.Authentication.TokenRevocationCleanupService>();
        _ = services.AddScoped<Services.Users.IUsersService, Services.Users.UsersService>();
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
        _ = services.AddScoped<IEmailService>(sp =>
        {
            IUnifiedLoggingService logger = sp.GetRequiredService<IUnifiedLoggingService>();
            EmailOptions opts = sp.GetRequiredService<EmailOptions>();
            IEmailTemplateRenderer renderer = sp.GetRequiredService<IEmailTemplateRenderer>();
            return opts.Mailjet?.ApiKey != null
                ? new MailjetEmailService(logger, opts, renderer)
                : new ConsoleEmailService(logger);
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

    #region Importing

    private static void RegisterImportingServices(IServiceCollection services)
    {
        _ = services.AddScoped<Importing.Services.Import.IImportParserService, Importing.Services.Import.ImportParserService>();
        _ = services.AddScoped<Importing.Services.Import.IImportProcessorService, Importing.Services.Import.ImportProcessorService>();
    }

    #endregion

    #region Catalog

    private static void RegisterCatalogServices(IServiceCollection services)
    {
        // Register cache adapter that wraps API-specific ICatalogCache for Infrastructure use
        _ = services.AddScoped<Farm.Infrastructure.Services.Catalog.Caching.ICatalogCacheProvider, Services.Catalog.CatalogCacheAdapter>();

        // Register Infrastructure catalog service with cache abstraction
        _ = services.AddScoped<Farm.Infrastructure.Services.Catalog.ICatalogService, Farm.Infrastructure.Services.Catalog.CatalogService>();

        // Register API adapter that wraps Infrastructure service to work with request DTOs
        _ = services.AddScoped<Services.Catalog.ICatalogService, Services.Catalog.CatalogServiceAdapter>();

        _ = services.AddScoped<Services.Filament.IFilamentTypeService, Services.Filament.FilamentTypeService>();
    }

    #endregion

    #region Slicing

    private static void RegisterSlicingServices(IServiceCollection services, IConfiguration configuration)
    {
        // Metrics
        _ = services.AddSingleton<Services.Slicing.SliceJobMetrics>();
        _ = services.AddSingleton<Services.Slicing.SlicerServiceMetrics>();

        // Configuration
        _ = services.Configure<Services.Workers.WorkerAuthSettings>(configuration.GetSection(Farm.Web.Api.Services.Workers.WorkerAuthSettings.SectionName));
        _ = services.AddSingleton<Services.Workers.IWorkerAuthService, Services.Workers.WorkerAuthService>();
        _ = services.Configure<Farm.Infrastructure.Settings.SlicerSettings>(configuration.GetSection(Farm.Infrastructure.Settings.SlicerSettings.SectionName));

        // Core slicing services
        _ = services.AddScoped<ISlicersService, SlicersService>();
        _ = services.AddScoped<IProfilesService, ProfilesService>();
        _ = services.AddScoped<Services.Slicing.IProfileParsingService, Services.Slicing.ProfileParsingService>();
        _ = services.AddScoped<Services.Slicing.IOrcaBundleParsingService, Services.Slicing.OrcaBundleParsingService>();
        _ = services.AddScoped<Services.Slicing.IOrcaPresetMappingService, Services.Slicing.OrcaPresetMappingService>();
        _ = services.AddScoped<Services.Slicing.IOrcaBundleExportService, Services.Slicing.OrcaBundleExportService>();

        // Job queue and orchestration
        _ = services.AddScoped<ISlicerJobQueue, Services.SlicerServices.DbSlicerJobQueue>();
        _ = services.AddSingleton<ISlicerProgressNotifier, Services.SlicerServices.SignalRSlicerProgressNotifier>();
        _ = services.AddScoped<ISlicerOrchestrator, Services.SlicerServices.SlicerOrchestrator>();
        _ = services.AddScoped<Services.Slicing.ISliceJobEventService, Services.Slicing.SliceJobEventService>();
        _ = services.AddScoped<Services.Queue.IQueueDataService, Services.Queue.QueueDataService>();
        _ = services.AddScoped<Services.Queue.IJobQueueService, Services.Queue.JobQueueService>();
        _ = services.AddScoped<IJobDispatcherService, JobDispatcherService>();

        // Job dispatch retry options
        _ = services.AddSingleton(sp =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            RetryOptions opts = new RetryOptions();
            cfg.GetSection("JobDispatchRetry").Bind(opts);
            return opts;
        });

        // Submission and file storage
        _ = services.AddScoped<Services.Slicing.ISlicingSubmissionService, Services.Slicing.SlicingSubmissionService>();
        _ = services.AddScoped<Services.SlicerServices.LocalSlicerFileStorage>();
        _ = services.AddScoped<ISlicerFileStorage>(sp => sp.GetRequiredService<Services.SlicerServices.LocalSlicerFileStorage>());

        // Slicer Library Registration (plugin discovery)
        _ = services
            .DiscoverAndRegisterSlicerPlugins()
            .AddSlicerRegistry();
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
            IUnifiedLoggingService logger = provider.GetRequiredService<IUnifiedLoggingService>();

            return new Farm.Infrastructure.Services.Printers.BackendClientFactory(serviceProvider, pluginRegistry, logger);
        });

        // Register the backend capability factory for capability-aware client retrieval
        // This factory now integrates with the plugin registry for backend metadata
        // while maintaining backward compatibility with reflection-based detection
        // IMPORTANT: Must be SCOPED because it depends on IBackendClientFactory which is scoped
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IBackendCapabilityFactory>(provider =>
        {
            IBackendClientFactory clientFactory = provider.GetRequiredService<Farm.Infrastructure.Services.Printers.IBackendClientFactory>();
            IUnifiedLoggingService logger = provider.GetRequiredService<IUnifiedLoggingService>();
            IBackendPluginRegistry? pluginRegistry = provider.GetService<Farm.Backend.Plugin.Core.IBackendPluginRegistry>();

            return new Farm.Infrastructure.Services.Printers.BackendCapabilityFactory(clientFactory, logger, pluginRegistry);
        });

        // Register the factory for getting printer status clients from plugins
        _ = services.AddSingleton<Farm.Infrastructure.Services.Printers.IPrinterStatusClientFactory>(provider =>
        {
            IServiceProvider serviceProvider = provider;
            IBackendPluginRegistry pluginRegistry = provider.GetRequiredService<Farm.Backend.Plugin.Core.IBackendPluginRegistry>();
            IUnifiedLoggingService logger = provider.GetRequiredService<IUnifiedLoggingService>();

            return new Farm.Infrastructure.Services.Printers.PrinterStatusClientFactory(serviceProvider, pluginRegistry, logger);
        });

        // Register the printer status cache (singleton in Infrastructure - shared across all layers)
        var printerStatusCache = new Farm.Infrastructure.Services.Printers.PrinterStatusCache();
        _ = services.AddSingleton<Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader>(printerStatusCache);
        _ = services.AddSingleton<Farm.Infrastructure.Services.Printers.IPrinterStatusCacheWriter>(printerStatusCache);

        // Register the printer status update receiver (scoped - one per request)
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterStatusUpdateReceiver, Farm.Infrastructure.Services.Printers.PrinterStatusUpdateReceiver>();

        // Register the printer status fallback service for timeout and circuit breaker management
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterStatusFallbackService, Farm.Infrastructure.Services.Printers.PrinterStatusFallbackService>();

        // Register the backend capabilities service for exposing plugin capabilities to the UI
        _ = services.AddScoped<Services.Printers.IPrinterBackendCapabilitiesService, Services.Printers.PrinterBackendCapabilitiesService>();

        // Register the multi-printer status coordinator for parallel operation orchestration
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IMultiPrinterStatusCoordinator, Farm.Infrastructure.Services.Printers.MultiPrinterStatusCoordinator>();

        // Register SignalR printer status broadcaster - abstracts real-time broadcasting for any UI implementation
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrinterStatusBroadcaster, Services.Printers.SignalRPrinterStatusBroadcaster>();

        // Register LocationService from Infrastructure layer - location management service
        _ = services.AddScoped<Farm.Infrastructure.Services.Locations.ILocationService, Farm.Infrastructure.Services.Locations.LocationService>();

        // Register PrintersService from Infrastructure layer - core business logic for any UI implementation
        _ = services.AddScoped<Farm.Infrastructure.Services.Printers.IPrintersService, Farm.Infrastructure.Services.Printers.PrintersService>();
    }

    #endregion

    #region Model and Gcode Services

    private static void RegisterModelAndGcodeServices(IServiceCollection services, IConfiguration configuration, bool disableBackgroundServices)
    {
        // Tag services
        _ = services.AddScoped<Services.Tags.ITagService, Services.Tags.TagService>();

        // SystemLogs service
        _ = services.AddScoped<Services.SystemLogs.ISystemLogService, Services.SystemLogs.SystemLogService>();

        // Folder management service (shared by model and gcode file services)
        _ = services.AddScoped<IFolderManagementService, FolderManagementService>();

        // Stored file operations service (consolidated file and thumbnail operations)
        _ = services.AddScoped<IStoredFileOperationsService, StoredFileOperationsService>();

        // Model services
        _ = services.AddScoped<Services.Model.IModel3DFileService, Services.Model.Model3DFileService>();
        _ = services.AddSingleton<IModelAnalysisService, ModelAnalysisService>();
        _ = services.AddSingleton<IVirusScanner, ClamAVVirusScanner>();
        _ = services.AddSingleton<IThumbnailGenerationService, ThumbnailGenerationService>();

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

        // Gcode upload settings and quota
        _ = services.AddSingleton<IGcodeUploadSettings, InMemoryGcodeUploadSettings>();
        _ = services.AddSingleton<IGcodeUploadQuotaService, InMemoryGcodeUploadQuotaService>();
    }

    #endregion

    #region Artifact Services

    private static void RegisterArtifactServices(IServiceCollection services, IConfiguration configuration, bool disableBackgroundServices)
    {
        _ = services.AddSingleton<Services.Artifacts.ArtifactsMetrics>();
        _ = services.Configure<ArtifactStorageSettings>(configuration.GetSection(Farm.Infrastructure.Settings.ArtifactStorageSettings.SectionName));
        _ = services.AddScoped<Services.Artifacts.IArtifactsService, Services.Artifacts.ArtifactsService>();
        _ = services.AddScoped<Services.Artifacts.IArtifactCleanupService, Services.Artifacts.ArtifactCleanupService>();

        if (!disableBackgroundServices)
        {
            _ = services.AddHostedService<Services.Artifacts.ArtifactCleanupHostedService>();
        }
    }

    #endregion

    #region Setup and Schema

    private static void RegisterSetupAndSchemaServices(IServiceCollection services)
    {
        _ = services.AddScoped<Services.Setup.ISetupService, Services.Setup.SetupService>();
        _ = services.AddScoped<Services.SchemaHealth.ISchemaHealthService, Services.SchemaHealth.SchemaHealthService>();
        _ = services.AddScoped<Services.SignalR.ISignalRTestService, Services.SignalR.SignalRTestService>();
    }

    private static void RegisterBackendClientPlugins(IServiceCollection services)
    {
        // Discover and register all backend client plugins
        // This will scan all loaded assemblies for IBackendClientPlugin implementations
        services.AddBackendClientPlugins();
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
        _ = services.AddHttpClient<ISpoolmanService, SpoolmanService>("SpoolmanService", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
    }

    #endregion

    #region Background Services

    private static void RegisterBackgroundServices(IServiceCollection services, bool disableBackgroundServices)
    {
        if (!disableBackgroundServices)
        {
            // System log cleanup (common service, not plugin-specific)
            _ = services.AddHostedService<SystemLogCleanupService>();

            // Stale worker cleanup service
            _ = services.AddHostedService<Services.Workers.StaleWorkerCleanupHostedService>();

            // Backend-specific background services are now registered by their respective plugins
            // via the IExtendedBackendPlugin.RegisterAdditionalServices() method:
            // - MoonrakerSubscriptionService (real-time WebSocket subscriptions)
            // - PrusaLinkPollingService (HTTP polling every 5 seconds)
            // - OctoPrintPollingService (HTTP polling every 10 seconds)
            // This keeps backend-specific logic encapsulated in plugins
        }
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
