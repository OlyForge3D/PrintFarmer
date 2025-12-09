using System;
using System.Diagnostics;
using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Slicing.Libraries;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Normalization;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Services.Email;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.JobDispatch;
using Farm.Web.Api.Services.RateLimiting;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Api.Services.Slicing.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

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
        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
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
        catch { }

        // Register a lightweight provider for system settings that reads from IConfiguration
        _ = services.AddSingleton<ISystemSettingsProvider, ConfigurationSystemSettingsProvider>();

        // Register SettingsService so DI constructs it with IConfiguration, AppDbContext and IUnifiedLoggingService
        _ = services.AddScoped<ISettingsService, SettingsService>();

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
    public static IServiceCollection AddPrintFarmerServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // AutoMapper
        _ = services.AddAutoMapper(typeof(Program).Assembly);

        // Check if background services should be disabled (for testing)
        bool disableBackgroundServices = ShouldDisableBackgroundServices();

        // Register services by category
        RegisterCoreInfrastructure(services);
        RegisterRepositories(services);
        RegisterTelemetryAndLogging(services);
        RegisterCachingServices(services);
        RegisterAuthenticationServices(services);
        RegisterEmailServices(services);
        RegisterRateLimitingServices(services);
        RegisterImportingServices(services);
        RegisterCatalogServices(services);
        RegisterSlicingServices(services, configuration);
        RegisterPrinterServices(services);
        RegisterModelAndGcodeServices(services, configuration, disableBackgroundServices);
        RegisterArtifactServices(services, configuration, disableBackgroundServices);
        RegisterSetupAndSchemaServices(services);
        RegisterHttpClients(services);
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

        // Network URL rewriting (stateless, safe as Singleton)
        _ = services.AddSingleton<INetworkUrlRewriteService, NetworkUrlRewriteService>();

        // Circuit breaker for resilient external calls
        _ = services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();

        // Storage path service for multi-deployment support (Docker and Kubernetes)
        _ = services.AddSingleton<Services.StorageManagement.IStoragePathService, Services.StorageManagement.StoragePathService>();

        // File Management Services
        _ = services.AddSingleton<Services.FileManagement.IFileManagementService, Services.FileManagement.FileManagementService>();
        _ = services.AddSingleton<Services.FileManagement.IFileIntegrityService, Services.FileManagement.FileIntegrityService>();
        _ = services.AddSingleton<Services.FileManagement.IChunkedUploadService, Services.FileManagement.ChunkedUploadService>();
        _ = services.AddSingleton<Services.FileManagement.IGcodeThumbnailExtractorService, Services.FileManagement.GcodeThumbnailExtractorService>();

        // File system abstraction (pure wrapper around static File/Directory APIs)
        _ = services.AddSingleton<Services.IO.IFileSystem, Services.IO.SystemFileSystem>();

        // Startup status tracking
        _ = services.AddSingleton<IStartupStatus, StartupStatus>();

        // Discovery progress cache for real-time updates
        _ = services.AddSingleton<Services.IDiscoveryProgressCache, Services.DiscoveryProgressCache>();

        // Discovery proxy service for streaming discovery with SignalR progress updates
        _ = services.AddScoped<Services.Interfaces.IDiscoveryProxyService, Services.DiscoveryProxyService>();
    }

    #endregion

    #region Repositories

    private static void RegisterRepositories(IServiceCollection services)
    {
        // Core repositories
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Printers.IPrintersRepository, Farm.Infrastructure.Repositories.Printers.EfPrintersRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Catalog.ICatalogRepository, Farm.Infrastructure.Repositories.Catalog.EfCatalogRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Users.IUsersRepository, Farm.Infrastructure.Repositories.Users.EfUsersRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.SystemLogs.ISystemLogRepository, Farm.Infrastructure.Repositories.SystemLogs.EfSystemLogRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.PrinterCapabilities.IPrinterCapabilitiesRepository, Farm.Infrastructure.Repositories.PrinterCapabilities.EfPrinterCapabilitiesRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Harvest.IHarvestRepository, Farm.Infrastructure.Repositories.Harvest.EfHarvestRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.FileConsistency.IFileConsistencyRepository, Farm.Infrastructure.Repositories.FileConsistency.EfFileConsistencyRepository>();

        // Tag repositories
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Tags.ITagRepository, Farm.Infrastructure.Repositories.Tags.EfTagRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Tags.IModelTagMappingRepository, Farm.Infrastructure.Repositories.Tags.EfModelTagMappingRepository>();

        // Queue repositories
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Queue.IQueueRepository, Farm.Infrastructure.Repositories.Queue.EfQueueRepository>();

        // Filament repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Filament.IFilamentTypeRepository, Farm.Infrastructure.Repositories.Filament.FilamentTypeRepository>();

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

        // Gcode repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Gcode.IGcodeRepository, Farm.Infrastructure.Repositories.Gcode.EfGcodeRepository>();

        // Model repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Model.IModelRepository, Farm.Infrastructure.Repositories.Model.EfModelRepository>();

        // Authentication audit repository
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Authentication.IAuthAuditLogRepository, Farm.Infrastructure.Repositories.Authentication.EfAuthAuditLogRepository>();
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
        _ = services.AddScoped<IPasswordHashingService, PasswordHashingService>();
        _ = services.AddScoped<IAuthenticationService, AuthenticationService>();
        _ = services.AddScoped<Services.PasswordPolicy.IPasswordPolicyService, Services.PasswordPolicy.PasswordPolicyService>();
        _ = services.AddScoped<Services.Authentication.IAccountLockoutService, Services.Authentication.AccountLockoutService>();
        _ = services.AddScoped<Services.Authentication.IAuthAuditService, Services.Authentication.AuthAuditService>();
        _ = services.AddScoped<Services.Authentication.ITokenRevocationService, Services.Authentication.TokenRevocationService>();
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
            return opts.Provider?.Equals("mailjet", StringComparison.OrdinalIgnoreCase) == true
                ? new Farm.Web.Api.Services.Email.MailjetEmailService(logger, opts, renderer)
                : new Farm.Web.Api.Services.Email.ConsoleEmailService(logger, renderer);
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
        _ = services.AddSingleton<IRateLimitService, InMemoryRateLimitService>();
    }

    #endregion

    #region Importing

    private static void RegisterImportingServices(IServiceCollection services)
    {
        _ = services.AddScoped<Importing.Services.Import.IImportParserService, Importing.Services.Import.ImportParserService>();
        _ = services.AddScoped<Importing.Services.Import.IImportProcessorService, Importing.Services.Import.ImportProcessorService>();
        _ = services.AddScoped<Importing.Services.Adapters.IPrinterCapabilityDiscoveryAdapter, Services.Adapters.PrinterCapabilityDiscoveryAdapter>();
        _ = services.AddScoped<Importing.Services.Adapters.IDefaultCatalogAdapter, Services.Adapters.DefaultCatalogAdapter>();
    }

    #endregion

    #region Catalog

    private static void RegisterCatalogServices(IServiceCollection services)
    {
        _ = services.AddScoped<Services.Catalog.ICatalogService, Services.Catalog.CatalogService>();
        _ = services.AddScoped<IDefaultCatalogService, DefaultCatalogService>();
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
        // This eliminates the need to pass individual backend clients (Moon, Prusa, SDCP, OctoPrint)
        // to PrintersService, making it easier to add new backends without modifying the constructor
        _ = services.AddSingleton<Services.Printers.IBackendClientFactory, Services.Printers.BackendClientFactory>();
        
        // Register the printer status client factory for backend-specific status retrieval
        _ = services.AddSingleton<Services.Printers.IPrinterStatusClientFactory, Services.Printers.PrinterStatusClientFactory>();
        
        // Register the printer status DTO builder for centralizing DTO construction logic
        _ = services.AddScoped<Services.Printers.IPrinterStatusDtoBuilder, Services.Printers.PrinterStatusDtoBuilder>();
        
        // Register the printer status fallback service for timeout and circuit breaker management
        _ = services.AddScoped<Services.Printers.IPrinterStatusFallbackService, Services.Printers.PrinterStatusFallbackService>();

        // Register the multi-printer status coordinator for parallel operation orchestration
        _ = services.AddScoped<Services.Printers.IMultiPrinterStatusCoordinator, Services.Printers.MultiPrinterStatusCoordinator>();

        _ = services.AddScoped<Services.Printers.IPrintersService, Services.Printers.PrintersService>();
        _ = services.AddScoped<Services.Interfaces.IMoonrakerDiagnosticsService, Services.MoonrakerDiagnosticsService>();
        _ = services.AddScoped<Services.Interfaces.IPrinterCapabilityDiscoveryService, Services.PrinterCapabilityDiscoveryService>();
        _ = services.AddScoped<Services.PrinterCapabilities.IPrinterCapabilitiesService, Services.PrinterCapabilities.PrinterCapabilitiesService>();
    }

    #endregion

    #region Model and Gcode Services

    private static void RegisterModelAndGcodeServices(IServiceCollection services, IConfiguration configuration, bool disableBackgroundServices)
    {
        // Tag services
        _ = services.AddScoped<Services.Tags.ITagService, Services.Tags.TagService>();

        // SystemLogs service
        _ = services.AddScoped<Services.SystemLogs.ISystemLogService, Services.SystemLogs.SystemLogService>();

        // Model services
        _ = services.AddScoped<Services.Model.IModelService, Services.Model.ModelService>();
        _ = services.AddSingleton<IModelAnalysisService, ModelAnalysisService>();
        _ = services.AddSingleton<IVirusScanner, ClamAVVirusScanner>();
        _ = services.AddSingleton<IThumbnailGenerationService, ThumbnailGenerationService>();

        // Harvest configuration and services
        _ = services.Configure<GcodeHarvestSettings>(configuration.GetSection(Farm.Infrastructure.Settings.GcodeHarvestSettings.SectionKey));
        _ = services.AddSingleton<IHarvestQueue, InMemoryHarvestQueue>();
        _ = services.AddScoped<IGcodeHarvestService, GcodeHarvestService>();
        _ = services.AddSingleton<IGcodeMetadataExtractorService, GcodeMetadataExtractorService>();
        _ = services.AddScoped<Services.Gcode.IGcodeFilesService, Services.Gcode.GcodeFilesService>();
        _ = services.AddScoped<Services.Gcode.IGcodeLibraryService, Services.Gcode.GcodeLibraryService>();

        // Gcode upload settings and quota
        _ = services.AddSingleton<IGcodeUploadSettings, InMemoryGcodeUploadSettings>();
        _ = services.AddSingleton<IGcodeUploadQuotaService, InMemoryGcodeUploadQuotaService>();

        // Harvest worker (background)
        if (!disableBackgroundServices)
        {
            _ = services.AddHostedService<HarvestWorkerService>();
        }
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

    #endregion

    #region HTTP Clients

    private static void RegisterHttpClients(IServiceCollection services)
    {
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
            // System log cleanup
            _ = services.AddHostedService<SystemLogCleanupService>();

            // Realtime update service for Klipper/Moonraker printers
            _ = services.AddHostedService<MoonrakerSubscriptionService>();

            // Polling update service for PrusaLink printers (HTTP polling every 5 seconds)
            _ = services.AddHostedService<PrusaLinkPollingService>();

            // Polling update service for OctoPrint printers (HTTP polling every 10 seconds)
            _ = services.AddHostedService<OctoPrintPollingService>();
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
