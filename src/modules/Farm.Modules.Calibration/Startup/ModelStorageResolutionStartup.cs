using Farm.Slicer.Module;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.Modules.Calibration.Startup;

/// <summary>
/// Registers <see cref="IModelStorageResolver"/> for split and microservices deployments (#2179).
/// </summary>
/// <remarks>
/// <para>
/// Monolith hosts load the slicer module in process, so <c>AddSlicerModule</c> already registers
/// the local filesystem-backed <see cref="Model3DStorageResolver"/> and this class does nothing.
/// </para>
/// <para>
/// Split and microservices hosts deliberately skip <c>AddSlicerModule</c> (see
/// <c>SlicerModuleExtensions.AddSlicerModule</c>'s early return), which previously left
/// <see cref="IModelStorageResolver"/> unregistered on the API host. That, in turn, made
/// <c>CalibrationCapabilityService.GetCapabilitiesAsync</c>'s
/// <c>modelStorageResolvable = _serviceProvider.GetService&lt;IModelStorageResolver&gt;() is not
/// null</c> check false by construction, so <c>calibrationSlicingOperational</c> could never
/// become true in that topology regardless of worker health.
/// </para>
/// <para>
/// Unlike <see cref="CalibrationProfileResolutionStartup"/>, this does not need an HTTP hop to
/// the slicer-host process: the split/microservices topology already shares the physical database
/// (<c>ConnectionStrings:Default</c>) and the model-storage volume between the API and slicer-host
/// containers (see <c>docker-compose.yml</c> / <c>docker-compose.slicer-host.yml</c>), and
/// <c>IStoragePathService</c> is already registered unconditionally on the API host (see
/// <c>ServiceCollectionExtensions</c>). So <see cref="Model3DStorageResolver"/> can be constructed
/// directly from a repository backed by that shared connection, exactly the same "shared-database"
/// pattern already proven by
/// <see cref="SlicerModuleExtensions.AddSlicerCalibrationProfileRepositories"/> for the
/// machine/process/filament profile repositories.
/// </para>
/// </remarks>
public static class ModelStorageResolutionStartup
{
    /// <summary>
    /// Registers <see cref="IModel3DFileRepository"/> and
    /// <see cref="IModelStorageResolver"/> for this host when it runs as a split or microservices
    /// deployment.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddModelStorageResolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!CalibrationProfileResolutionStartup.IsSplitDeployment(configuration))
        {
            // Monolith hosts already have IModelStorageResolver via AddSlicerModule.
            return services;
        }

        // Idempotency + "already registered by AddSlicerModule" guard, matching
        // AddSlicerCalibrationProfileRepositories: no-op when a resolver is already present.
        if (services.Any(sd => sd.ServiceType == typeof(IModelStorageResolver)))
        {
            return services;
        }

        _ = services.AddSlicerCalibrationProfileRepositories(configuration);

        // Insurance against AddSlicerCalibrationProfileRepositories' own early return: it no-ops
        // whenever IMachineProfileRepository is already registered by *some other* caller, which
        // today always also means SlicerDbContext and IModel3DFileRepository were registered
        // alongside it (all three are added together, so they cannot diverge in practice). Should
        // a future caller ever register IMachineProfileRepository without the rest of that chain,
        // these two TryAdd/Ensure calls guarantee Model3DStorageResolver still has a fully
        // resolvable dependency chain instead of throwing out of
        // CalibrationCapabilityService.GetCapabilitiesAsync's GetService<T>() call.
        services.EnsureSlicerDatabaseRegistered(configuration);
        services.TryAddScoped<IModel3DFileRepository, EfModel3DFileRepository>();
        _ = services.AddScoped<IModelStorageResolver, Model3DStorageResolver>();

        return services;
    }
}
