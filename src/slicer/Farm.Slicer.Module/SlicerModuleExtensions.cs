using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
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
    /// repositories, metrics, and configuration POCOs.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (reads DB_PROVIDER, ConnectionStrings:Default, etc.).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlicerModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddSlicerDatabase(services, configuration);
        AddSlicerRepositories(services);
        AddSlicerMetrics(services);
        AddSlicerConfiguration(services, configuration);

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
            ?? "Data Source=slicer.db";

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
            _ = options.UseSqlServer(connectionString);
        }
        else if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
              || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
        {
            _ = options.UseNpgsql(connectionString);
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
        _ = services.Configure<ArtifactStorageSettings>(
            configuration.GetSection(ArtifactStorageSettings.SectionName));
    }
}
