using Farm.Infrastructure.Services;
using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Api.HostedServices;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Repositories;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Api.Services.Adapters;
using Farm.Slicer.Module.Repositories;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api;

/// <summary>
/// Extension methods for registering slicer API controllers and SignalR hubs.
/// </summary>
public static class SlicerApiExtensions
{
    /// <summary>
    /// Adds the slicer module API assembly as an MVC application part so its
    /// controllers are discovered by the routing infrastructure.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers</c>.</param>
    /// <returns>The MVC builder for chaining.</returns>
    public static IMvcBuilder AddSlicerControllers(this IMvcBuilder builder)
    {
        _ = builder.AddApplicationPart(typeof(SlicerApiExtensions).Assembly);
        return builder;
    }

    /// <summary>
    /// Registers slicer API-layer services (SignalR notifiers, job dispatch, profile mapping) into the DI container.
    /// </summary>
    public static IServiceCollection AddSlicerApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        // SignalR notifiers
        _ = services.AddSingleton<ISlicerProgressNotifier, SignalRSlicerProgressNotifier>();
        _ = services.AddScoped<ISliceJobEventService, SliceJobEventService>();

        // Profile mapping and export
        _ = services.AddScoped<IOrcaPresetMappingService, OrcaPresetMappingService>();
        _ = services.AddScoped<IOrcaBundleExportService, OrcaBundleExportService>();

        // File storage and submission
        _ = services.AddScoped<LocalSlicerFileStorage>();
        _ = services.AddScoped<ISlicerFileStorage>(sp => sp.GetRequiredService<LocalSlicerFileStorage>());
        _ = services.AddScoped<ISlicingSubmissionService, SlicingSubmissionService>();

        // Core slicing services
        _ = services.AddScoped<ISlicersService, SlicersService>();
        _ = services.AddScoped<IProfilesService, ProfilesService>();
        _ = services.AddSingleton<IWorkerAuthService, WorkerAuthService>();

        // Artifact services
        _ = services.Configure<Farm.Infrastructure.Settings.ArtifactStorageSettings>(configuration.GetSection(Farm.Infrastructure.Settings.ArtifactStorageSettings.SectionName));
        _ = services.AddScoped<IArtifactsService, ArtifactsService>();
        _ = services.AddScoped<IArtifactCleanupService, ArtifactCleanupService>();
        _ = services.AddHostedService<ArtifactCleanupHostedService>();

        // Host-independent adapters (bridge module interfaces → infrastructure services)
        _ = services.AddSingleton<IRateLimitService, ModuleRateLimitAdapter>();
        _ = services.AddScoped<ICatalogServiceAdapter, ModuleCatalogServiceAdapter>();
        _ = services.AddScoped<ISlicerFileManagementService, ModuleFileManagementAdapter>();
        _ = services.AddScoped<ISlicerStoredFileOpsService, ModuleStoredFileOpsAdapter>();
        _ = services.AddSingleton<ISlicerTempPathProvider, DefaultSlicerTempPathProvider>();

        // Repositories migrated from Farm.Infrastructure (use SlicerDbContext for Model3D)
        _ = services.AddScoped<IFileConsistencyRepository, EfFileConsistencyRepository>();
        _ = services.AddScoped<IFileAuditRepository, EfFileAuditRepository>();

        // Infrastructure abstractions for cross-module queries
        _ = services.AddScoped<IModel3DQueryProvider, SlicerModel3DQueryProvider>();
        _ = services.AddScoped<IProfileImportService, SlicerProfileImportService>();

        // Background services
        _ = services.AddHostedService<ProfileTaskCheckService>();

        return services;
    }

    /// <summary>
    /// Maps slicer-module SignalR hubs to their endpoint routes.
    /// Maps <see cref="SlicerHub"/> to <c>/hubs/slicer-registry</c> and
    /// <see cref="SlicerProgressHub"/> to <c>/hubs/slicers</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (typically from <c>app.MapXxx</c>).</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapSlicerHubs(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.MapHub<SlicerHub>("/hubs/slicer-registry");
        _ = endpoints.MapHub<SlicerProgressHub>("/hubs/slicers");
        return endpoints;
    }

    /// <summary>
    /// Configures artifact storage metrics thresholds and alert subscriptions.
    /// Call after the application is built (requires <see cref="IServiceProvider"/>).
    /// </summary>
    public static void ConfigureSlicerMetrics(this WebApplication app)
    {
        try
        {
            ArtifactStorageSettings settings = app.Services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ArtifactStorageSettings>>().Value;
            ArtifactsMetrics metrics = app.Services.GetRequiredService<ArtifactsMetrics>();

            if (!settings.EnableStorageAlerts)
            {
                return;
            }

            metrics.SetThresholds(settings.StorageWarningThresholdBytes, settings.StorageCriticalThresholdBytes);

            metrics.ThresholdExceeded += (_, e) =>
            {
                ILogger? logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("Farm.Slicer.Module.Api");
                string levelStr = e.Level switch
                {
                    SlicerStorageThresholdLevel.Warning => "WARNING",
                    SlicerStorageThresholdLevel.Critical => "CRITICAL",
                    _ => "UNKNOWN"
                };

                logger?.LogWarning(
                    "[ArtifactStorage] {Level} threshold exceeded: {CurrentGB:F2} GB (Warning: {WarningGB:F2} GB, Critical: {CriticalGB:F2} GB)",
                    levelStr,
                    e.CurrentBytes / (1024.0 * 1024 * 1024),
                    e.WarningThreshold / (1024.0 * 1024 * 1024),
                    e.CriticalThreshold / (1024.0 * 1024 * 1024));
            };
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "[Startup] Failed to configure artifact storage thresholds");
        }
    }
}
