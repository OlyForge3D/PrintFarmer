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
    /// Registers all slicer-module owned services: <see cref="SlicerDbContext"/>,
    /// repositories, metrics, configuration POCOs, and hosted services.
    /// When <c>Slicer:Enabled</c> is <c>false</c>, nothing is registered
    /// (zero slicer footprint in the host process).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (reads DB_PROVIDER, ConnectionStrings:Default, etc.).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlicerModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        bool enabled = configuration.GetValue("Slicer:Enabled", true);
        if (!enabled)
        {
            return services;
        }

        AddSlicerDatabase(services, configuration);
        AddSlicerRepositories(services);
        AddSlicerServices(services);
        AddSlicerMetrics(services);
        AddSlicerConfiguration(services, configuration);
        AddSlicerHostedServices(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers <see cref="SlicerDbContext"/> with multi-provider support
    /// (SQLite, PostgreSQL, SQL Server) and a <see cref="IDbContextFactory{TContext}"/>
    /// for singleton consumers.
    /// </summary>
    private static void AddSlicerDatabase(IServiceCollection services, IConfiguration configuration)
    {
        string? providerRaw = configuration.GetValue<string>("DB_PROVIDER");
        string provider = string.IsNullOrWhiteSpace(providerRaw) ? "sqlite" : providerRaw.Trim();

        string connectionString = configuration.GetConnectionString("Default")
            ?? configuration.GetValue<string>("DB_CONNECTION")
            ?? "Data Source=farm.db";

        _ = services.AddDbContext<SlicerDbContext>(options =>
            ConfigureProvider(options, provider, connectionString));

        // Factory for singletons that cannot accept a scoped DbContext directly.
        DbContextOptionsBuilder<SlicerDbContext> optionsBuilder = new();
        ConfigureProvider(optionsBuilder, provider, connectionString);
        _ = services.AddSingleton(optionsBuilder.Options);
        _ = services.AddDbContextFactory<SlicerDbContext>();
    }

    /// <summary>
    /// Configures the EF Core provider on <paramref name="options"/> based on
    /// the <paramref name="provider"/> string (sqlite, postgres, sqlserver).
    /// </summary>
    private static void ConfigureProvider(
        DbContextOptionsBuilder options,
        string provider,
        string connectionString)
    {
        if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            _ = options.UseSqlServer(
                connectionString,
                x => x.MigrationsAssembly("Farm.Slicer.Migrations.SqlServer"));
        }
        else if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
              || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
        {
            _ = options.UseNpgsql(
                connectionString,
                x => x.MigrationsAssembly("Farm.Slicer.Migrations.PostgreSQL"));
        }
        else
        {
            _ = options.UseSqlite(connectionString);
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

        // Plugin discovery runs during startup via extension method DiscoverAndRegisterSlicerPlugins()
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
    }

    /// <summary>
    /// Registers slicer background/hosted services for worker monitoring,
    /// job dispatching, timeout scanning, and stale worker cleanup.
    /// </summary>
    private static void AddSlicerHostedServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddHostedService<WorkerHealthMonitorService>();
        _ = services.AddHostedService<JobDispatchingService>();

        // Error recovery: scan for stuck slice jobs and requeue/fail according to retry policy
        _ = services.Configure<JobDispatchRetrySettings>(configuration.GetSection("JobDispatchRetry"));

        _ = services.Configure<CircuitBreakerSettings>(configuration.GetSection("CircuitBreaker"));
        _ = services.AddSingleton<IWorkerCircuitBreakerService, WorkerCircuitBreakerService>();

        _ = services.AddHostedService<JobTimeoutScannerHostedService>();

        // Stale worker cleanup service
        _ = services.Configure<StaleWorkerCleanupSettings>(
            configuration.GetSection(StaleWorkerCleanupSettings.SectionName));
        _ = services.AddHostedService<StaleWorkerCleanupHostedService>();
    }
}
