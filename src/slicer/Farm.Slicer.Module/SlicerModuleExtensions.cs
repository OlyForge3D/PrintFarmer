using Farm.Infrastructure.Data;
using Farm.Infrastructure.PrinterCalibration;
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
#pragma warning disable S2094 // Classes should not be empty — intentional DI marker type
    private sealed class SlicerModuleMarker;
#pragma warning restore S2094

    /// <summary>
    /// Marker service used to detect whether
    /// <see cref="AddSlicerCalibrationProfileRepositories"/> has already registered its
    /// repositories, preventing duplicate registrations when called more than once.
    /// </summary>
#pragma warning disable S2094 // Classes should not be empty — intentional DI marker type
    private sealed class SlicerCalibrationProfileRepositoriesMarker;
#pragma warning restore S2094

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

        // In split deployments, the main API does not load the slicer module inline —
        // it runs in a separate slicer-host process. The user-facing SlicerSettings.Enabled
        // is a separate concern, set dynamically when a worker registers.
        string? deploymentMode =
            configuration.GetValue<string>("DEPLOYMENT_MODE") ??
            configuration.GetValue<string>("Deployment:Mode");
        if (string.Equals(deploymentMode, "microservices", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(deploymentMode, "split", StringComparison.OrdinalIgnoreCase))
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
    /// Registers only the machine/process/filament profile repositories (and the
    /// <see cref="SlicerDbContext"/> they depend on) without loading the rest of the slicer
    /// module: no plugin discovery, no hosted services, no job orchestration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split and microservices hosts deliberately skip <see cref="AddSlicerModule"/>, so
    /// <c>Farm.Web.Api.Services.Startup.MoonrakerEmulatorSeeder</c> — which runs on the
    /// API host in every deployment topology, not just monolith — had no
    /// <c>IMachineProfileRepository</c>/<c>IProcessProfileRepository</c>/
    /// <c>IFilamentProfileRepository</c> registered when resolving them from its DI scope,
    /// throwing and turning the daily-validation reset endpoint into an unconditional 500 (#1858).
    /// </para>
    /// <para>
    /// This does not reuse the HTTP-hop pattern used by
    /// <c>CalibrationProfileResolutionStartup</c> (routing to the slicer-host process; the
    /// equivalent capability-client startup wiring was removed with the calibration generation
    /// saga by #1979) because the seeder only needs to read/write a small,
    /// deterministic set of content-hash-keyed <b>system</b> profiles for an explicitly opt-in,
    /// disabled-by-default validation feature
    /// (<c>MoonrakerEmulatorSeed:Enabled</c>). The API and slicer-host containers already point at
    /// the same physical database in every documented split/microservices deployment (same
    /// <c>ConnectionStrings:Default</c>), and the API project already carries a compile-time
    /// reference to this assembly for the slicer domain/data types it uses elsewhere — so opening
    /// a second, narrowly-scoped <see cref="SlicerDbContext"/> connection here is both safe and far
    /// simpler than standing up a new authenticated slicer-host endpoint for three rows of
    /// fixture data.
    /// </para>
    /// <para>
    /// No-ops when <see cref="IMachineProfileRepository"/> is already registered (monolith hosts,
    /// where <see cref="AddSlicerModule"/> ran its full registration path) or when this method has
    /// already run once. Deliberately does NOT gate on <see cref="AddSlicerModule"/>'s own marker:
    /// that marker is added unconditionally, before <see cref="AddSlicerModule"/>'s split/
    /// microservices early return, so on a "split"-mode host the marker would be present even
    /// though the repositories were never registered — checking the marker here would silently
    /// no-op on exactly the split-mode hosts this method exists to cover. Registers no hosted
    /// services, plugin discovery, or anything beyond the three repositories and the DbContext
    /// they need.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (reads DB_PROVIDER, ConnectionStrings:Default, etc.).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlicerCalibrationProfileRepositories(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Do NOT gate on SlicerModuleMarker here: AddSlicerModule adds that marker
        // unconditionally, before its own split/microservices early return (see above), so on a
        // "split"-mode host (as opposed to "microservices", which Program.cs never calls
        // AddSlicerModule for at all) the marker is present even though AddSlicerModule returned
        // early WITHOUT registering these repositories. Gating on the marker would silently no-op
        // this method on exactly the split-mode hosts it exists to cover. Instead, check whether
        // the repositories themselves are already registered — true only when AddSlicerModule ran
        // its full (monolith) registration path.
        if (services.Any(sd => sd.ServiceType == typeof(IMachineProfileRepository)))
        {
            return services;
        }

        // Idempotency guard: skip if this method already registered its repositories.
        if (services.Any(sd => sd.ServiceType == typeof(SlicerCalibrationProfileRepositoriesMarker)))
        {
            return services;
        }

        _ = services.AddSingleton<SlicerCalibrationProfileRepositoriesMarker>();

        AddSlicerDatabase(services, configuration);
        _ = services.AddScoped<IMachineProfileRepository, EfMachineProfileRepository>();
        _ = services.AddScoped<IProcessProfileRepository, EfProcessProfileRepository>();
        _ = services.AddScoped<IFilamentProfileRepository, EfFilamentProfileRepository>();

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
        // Use Scoped lifetime to match the scoped DbContextOptions registered by AddDbContext.
        _ = services.AddDbContextFactory<SlicerDbContext>(
            options => ConfigureProvider(options, dbConfig),
            ServiceLifetime.Scoped);
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
            _ = options.UseSqlite(
                dbConfig.ConnectionString,
                x => x.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"));
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
        _ = services.AddScoped<ICalibrationProfileResolver, CalibrationProfileResolver>();
        _ = services.AddScoped<IModelStorageResolver, Model3DStorageResolver>();
        _ = services.AddScoped<IUnifiedFilesQueryService, UnifiedFilesQueryService>();
        _ = services.AddScoped<ISlicerJobQueue, DbSlicerJobQueue>();
        _ = services.AddScoped<ISlicerOrchestrator, SlicerOrchestrator>();
        _ = services.AddScoped<IOrcaBundleParsingService, OrcaBundleParsingService>();
        _ = services.AddScoped<IProfileParsingService, ProfileParsingService>();
        _ = services.AddScoped<ISlicerProfileParsingService>(sp => sp.GetRequiredService<IProfileParsingService>() as ISlicerProfileParsingService
            ?? throw new InvalidOperationException("ProfileParsingService must implement ISlicerProfileParsingService"));
        _ = services.AddSingleton<IThreeMfMetadataService, ThreeMfMetadataService>();

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
        // Database initialization (one-shot, applies provider-specific migrations on startup)
        _ = services.AddHostedService<SlicerDbInitializationHostedService>();

        _ = services.AddHostedService<WorkerHealthMonitorService>();

        // Refreshes the SlicerServiceMetrics capacity snapshot out-of-band so its
        // observable gauges never run database work on the OpenTelemetry collection
        // thread nor capture a scoped service's `this` (see #1676).
        _ = services.AddHostedService<SlicerCapacityMetricsRefreshService>();

        // Error recovery: scan for stuck slice jobs and requeue/fail according to retry policy
        _ = services.Configure<JobDispatchRetrySettings>(configuration.GetSection("JobDispatchRetry"));

        _ = services.Configure<CircuitBreakerSettings>(configuration.GetSection("CircuitBreaker"));
        _ = services.AddSingleton<IWorkerCircuitBreakerService, WorkerCircuitBreakerService>();

        _ = services.AddHostedService<JobTimeoutScannerHostedService>();

        // Stale worker cleanup service
        _ = services.AddHostedService<StaleWorkerCleanupHostedService>();
    }
}
