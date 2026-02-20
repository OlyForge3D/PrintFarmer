using Farm.Infrastructure.Data;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.HostedServices;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module;

/// <summary>
/// Extension methods for registering slicer-module services with the DI container.
/// </summary>
public static class SlicerModuleExtensions
{
    /// <summary>
    /// Marker service used to detect whether <see cref="AddSlicerModule"/> has already been called,
    /// preventing duplicate registrations when called more than once.
    /// </summary>
    private sealed class SlicerModuleMarker;

    /// <summary>
    /// Registers all slicer-module owned services: <see cref="SlicerDbContext"/>,
    /// repositories, metrics, configuration POCOs, and hosted services.
    /// When <c>Slicer:Enabled</c> is <c>false</c>, nothing is registered
    /// (zero slicer footprint in the host process).
    /// This method is idempotent — calling it multiple times has no additional effect.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (reads DB_PROVIDER, ConnectionStrings:Default, etc.).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlicerModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Idempotency guard: skip if already registered
        if (services.Any(sd => sd.ServiceType == typeof(SlicerModuleMarker)))
        {
            return services;
        }

        _ = services.AddSingleton<SlicerModuleMarker>();

        bool enabled = configuration.GetValue("Slicer:Enabled", true);
        if (!enabled)
        {
            return services;
        }

        AddSlicerDatabase(services, configuration);
        AddSlicerRepositories(services);

        // Load runtime plugin assemblies from directory (if configured) before discovery
        string? pluginsPath = configuration.GetValue<string>("Slicer:PluginsPath");
        SlicerPluginDiscovery.LoadPluginAssemblies(pluginsPath);

        AddSlicerServices(services);
        AddSlicerMetrics(services);
        AddSlicerConfiguration(services, configuration);
        AddSlicerHostedServices(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers <see cref="SlicerDbContext"/> with multi-provider support
    /// (SQLite, PostgreSQL, SQL Server) and a <see cref="IDbContextFactory{TContext}"/>
    /// for singleton consumers. Uses <see cref="DatabaseProviderConfiguration"/> from
    /// Farm.Infrastructure for consistent provider resolution.
    /// </summary>
    private static void AddSlicerDatabase(IServiceCollection services, IConfiguration configuration)
    {
        DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(configuration);

        _ = services.AddDbContext<SlicerDbContext>(options =>
            ConfigureProvider(options, dbConfig));

        // Register a factory that shares the same configuration as AddDbContext.
        _ = services.AddDbContextFactory<SlicerDbContext>(options =>
            ConfigureProvider(options, dbConfig));
    }

    /// <summary>
    /// Configures the EF Core provider on <paramref name="options"/> based on
    /// the resolved <paramref name="dbConfig"/>.
    /// </summary>
    private static void ConfigureProvider(
        DbContextOptionsBuilder options,
        DatabaseProviderConfiguration dbConfig)
    {
        if (dbConfig.IsSqlServer)
        {
            _ = options.UseSqlServer(
                dbConfig.ConnectionString,
                x => x.MigrationsAssembly("Farm.Slicer.Migrations.SqlServer"));
        }
        else if (dbConfig.IsPostgres)
        {
            _ = options.UseNpgsql(
                dbConfig.ConnectionString,
                x => x.MigrationsAssembly("Farm.Slicer.Migrations.PostgreSQL"));
        }
        else
        {
            _ = options.UseSqlite(dbConfig.ConnectionString);
        }
    }

    /// <summary>
    /// Registers all repository interface → EF implementation pairs as scoped services.
    /// </summary>
    private static void AddSlicerRepositories(IServiceCollection services)
    {
        _ = services.AddScoped<IArtifactsRepository, EfArtifactsRepository>();
        _ = services.AddScoped<IFilamentProfileRepository, EfFilamentProfileRepository>();
        _ = services.AddScoped<IMachineModelProfileRepository, EfMachineModelProfileRepository>();
        _ = services.AddScoped<IMachineProfileRepository, EfMachineProfileRepository>();
        _ = services.AddScoped<IModel3DFileRepository, EfModel3DFileRepository>();
        _ = services.AddScoped<IProcessProfileRepository, EfProcessProfileRepository>();
        _ = services.AddScoped<IProfilesRepository, EfProfilesRepository>();
        _ = services.AddScoped<ISliceJobRepository, EfSliceJobRepository>();
        _ = services.AddScoped<ISlicersRepository, EfSlicersRepository>();
        _ = services.AddScoped<IWorkerRepository, EfWorkerRepository>();
    }

    /// <summary>
    /// Registers module-level service implementations (business logic).
    /// </summary>
    private static void AddSlicerServices(IServiceCollection services)
    {
        _ = services.AddScoped<ISlicerJobQueue, DbSlicerJobQueue>();
        _ = services.AddScoped<ISlicerOrchestrator, SlicerOrchestrator>();
        _ = services.AddScoped<IOrcaBundleParsingService, OrcaBundleParsingService>();
        _ = services.AddScoped<IProfileParsingService, ProfileParsingService>();
        _ = services.AddScoped<ISlicerProfileParsingService>(sp => sp.GetRequiredService<IProfileParsingService>() as ISlicerProfileParsingService
            ?? throw new InvalidOperationException("ProfileParsingService must implement ISlicerProfileParsingService"));

        // Discover slicer engine plugins (OrcaSlicer, PrusaSlicer, etc.) and build registry
        _ = services
            .DiscoverAndRegisterSlicerPlugins()
            .AddSlicerRegistry();
    }

    /// <summary>
    /// Registers metrics singletons for slicer telemetry.
    /// </summary>
    private static void AddSlicerMetrics(IServiceCollection services)
    {
        _ = services.AddSingleton<SliceJobMetrics>();
        _ = services.AddSingleton<SlicerServiceMetrics>();
        _ = services.AddSingleton<ArtifactsMetrics>();
    }

    /// <summary>
    /// Binds configuration sections to strongly-typed settings POCOs.
    /// </summary>
    private static void AddSlicerConfiguration(IServiceCollection services, IConfiguration configuration)
    {
        _ = services.Configure<WorkerAuthSettings>(
            configuration.GetSection(WorkerAuthSettings.SectionName));
        _ = services.Configure<StaleWorkerCleanupSettings>(
            configuration.GetSection(StaleWorkerCleanupSettings.SectionName));
        _ = services.Configure<SlicerArtifactStorageSettings>(
            configuration.GetSection(SlicerArtifactStorageSettings.SectionName));
        _ = services.Configure<Farm.Slicer.Module.Settings.SlicerSettings>(
            configuration.GetSection(Farm.Slicer.Module.Settings.SlicerSettings.SectionName));
    }

    /// <summary>
    /// Registers slicer background/hosted services for worker monitoring,
    /// job dispatching, timeout scanning, and stale worker cleanup.
    /// </summary>
    private static void AddSlicerHostedServices(IServiceCollection services, IConfiguration configuration)
    {
        // Database initialization (one-shot, runs EnsureCreated on startup)
        _ = services.AddHostedService<SlicerDbInitializationHostedService>();

        _ = services.AddHostedService<WorkerHealthMonitorService>();
        _ = services.AddHostedService<JobDispatchingService>();

        // Error recovery: scan for stuck slice jobs and requeue/fail according to retry policy
        _ = services.Configure<JobDispatchRetrySettings>(configuration.GetSection("JobDispatchRetry"));

        _ = services.Configure<CircuitBreakerSettings>(configuration.GetSection("CircuitBreaker"));
        _ = services.AddSingleton<IWorkerCircuitBreakerService, WorkerCircuitBreakerService>();

        _ = services.AddHostedService<JobTimeoutScannerHostedService>();

        // Stale worker cleanup service
        _ = services.AddHostedService<StaleWorkerCleanupHostedService>();
    }
}
