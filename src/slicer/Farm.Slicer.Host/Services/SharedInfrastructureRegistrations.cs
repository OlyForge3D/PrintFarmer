using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Farm.Slicer.Host.Services;

/// <summary>
/// Registers infrastructure services that the slicer-host needs when running
/// standalone in microservices mode. These services bridge AppDbContext tables
/// shared between the main API and slicer-host (tags, folders, catalog, settings).
/// </summary>
/// <remarks>
/// In monolithic mode, these services are provided by the API's DI container.
/// In standalone mode, the slicer-host connects to the same PostgreSQL database
/// and registers the same infrastructure implementations locally.
/// </remarks>
public static class SharedInfrastructureRegistrations
{
    /// <summary>
    /// Registers AppDbContext and all infrastructure services required by
    /// Model3DFileService, SlicersService, and ProfilesService.
    /// </summary>
    public static IServiceCollection AddSharedInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddAppDatabase(services, configuration);
        AddRepositories(services);
        AddFileServices(services);
        AddCatalogServices(services);
        AddSettingsAndAliasServices(services);

        return services;
    }

    /// <summary>
    /// Registers <see cref="AppDbContext"/> using the same connection string
    /// and provider as SlicerDbContext. The slicer-host does not run AppDbContext
    /// migrations — the main API handles those.
    /// </summary>
    private static void AddAppDatabase(IServiceCollection services, IConfiguration configuration)
    {
        DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(configuration);

        services.AddDbContext<AppDbContext>(options =>
        {
            if (dbConfig.IsSqlServer)
            {
                options.UseSqlServer(dbConfig.ConnectionString);
            }
            else if (dbConfig.IsPostgres)
            {
                options.UseNpgsql(dbConfig.ConnectionString);
            }
            else
            {
                options.UseSqlite(dbConfig.ConnectionString);
            }
        });

        services.AddDbContextFactory<AppDbContext>(options =>
        {
            if (dbConfig.IsSqlServer)
            {
                options.UseSqlServer(dbConfig.ConnectionString);
            }
            else if (dbConfig.IsPostgres)
            {
                options.UseNpgsql(dbConfig.ConnectionString);
            }
            else
            {
                options.UseSqlite(dbConfig.ConnectionString);
            }
        });

        // Data Protection for ISensitiveDataProtector (used by AppUnitOfWork)
        services.AddDataProtection();
        services.AddSingleton<ISensitiveDataProtector, SensitiveDataProtector>();
    }

    /// <summary>
    /// Registers repositories backed by AppDbContext (tags, catalog, settings, UoW).
    /// </summary>
    private static void AddRepositories(IServiceCollection services)
    {
        services.AddScoped<IUnitOfWork, AppUnitOfWork>();
        services.AddScoped<ITagRepository, EfTagRepository>();
        services.AddScoped<ICatalogRepository, EfCatalogRepository>();
        services.AddScoped<IAppSettingsRepository, EfAppSettingsRepository>();
    }

    /// <summary>
    /// Registers file system, storage path, and file management services.
    /// These are stateless or configuration-only (no DbContext dependency).
    /// </summary>
    private static void AddFileServices(IServiceCollection services)
    {
        services.AddSingleton<Farm.Infrastructure.IO.IFileSystem, Farm.Infrastructure.IO.SystemFileSystem>();
        services.AddScoped<IFileManagementService, FileManagementService>();
        services.AddScoped<IStoredFileOperationsService, StoredFileOperationsService>();
        services.AddScoped<IFolderManagementService, FolderManagementService>();

        // Application path provider (bridges IWebHostEnvironment to Infrastructure layer)
        services.AddSingleton<IApplicationPathProvider, SlicerHostPathProvider>();
        services.AddSingleton<IStoragePathService, StoragePathService>();
    }

    /// <summary>
    /// Registers the full <see cref="ICatalogService"/> backed by AppDbContext
    /// rather than HTTP. This is faster and avoids circular calls since
    /// slicer-host shares the same database.
    /// </summary>
    private static void AddCatalogServices(IServiceCollection services)
    {
        services.AddScoped<INormalizationEventLogger, NormalizationEventLogger>();
        services.AddSingleton<ICatalogCacheProvider, NullCatalogCacheProvider>();
        services.AddScoped<ICatalogService, CatalogService>();
    }

    /// <summary>
    /// Registers settings and printer model alias services backed by AppDbContext.
    /// </summary>
    private static void AddSettingsAndAliasServices(IServiceCollection services)
    {
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IPrinterModelAliasService, PrinterModelAliasService>();

        // Rate limiting (required by ModuleRateLimitAdapter in SlicerApiExtensions)
        services.AddSingleton(sp =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            RateLimitOptions opts = new RateLimitOptions();
            cfg.GetSection("RateLimiting").Bind(opts);
            return opts;
        });
        services.AddSingleton<Farm.Infrastructure.Services.RateLimiting.IRateLimitService, InMemoryRateLimitService>();
    }

    /// <summary>
    /// No-op cache provider for catalog data in the slicer-host.
    /// The slicer-host queries the database directly and doesn't need
    /// the full caching layer that the API uses.
    /// </summary>
    private sealed class NullCatalogCacheProvider : ICatalogCacheProvider
    {
        public Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct)
            => Task.FromResult<(IReadOnlyList<ManufacturerDto>, string?)>(([], null));

        public Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
            => Task.FromResult<(IReadOnlyList<PrinterModelDto>, string?)>(([], null));

        public void InvalidateManufacturers()
        {
        }

        public void InvalidateModels(Guid? manufacturerId = null)
        {
        }
    }

    /// <summary>
    /// Path provider for the slicer-host process. Wraps <see cref="IWebHostEnvironment"/>
    /// to provide content/web root paths to infrastructure services.
    /// </summary>
    private sealed class SlicerHostPathProvider(IWebHostEnvironment environment) : IApplicationPathProvider
    {
        public string GetContentRootPath() => environment.ContentRootPath;

        public string GetWebRootPath() => environment.WebRootPath;
    }
}
