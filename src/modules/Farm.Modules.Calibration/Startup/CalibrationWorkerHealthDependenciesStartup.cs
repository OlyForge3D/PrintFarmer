using Farm.Slicer.Module;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Calibration.Startup;

/// <summary>
/// Registers <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> of
/// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/> for split and microservices deployments
/// (#2178), so
/// <see cref="Farm.Modules.Calibration.Services.Capabilities.CalibrationCapabilityService.GetWorkerHealthAsync"/>
/// can query worker health independently of any other startup wiring.
/// </summary>
/// <remarks>
/// <para>
/// Monolith hosts load the slicer module in process, so <c>AddSlicerModule</c> already registers
/// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/> and its
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>, and this class does
/// nothing.
/// </para>
/// <para>
/// Split and microservices hosts deliberately skip <c>AddSlicerModule</c> (see
/// <c>SlicerModuleExtensions.AddSlicerModule</c>'s early return). That previously left
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> of
/// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/> registered on such a host only as a side
/// effect of two unrelated startup paths: <c>Farm.Web.Api.Startup.MoonrakerEmulatorSeederDependenciesStartup</c>'s
/// call chain (<c>AddMoonrakerEmulatorSeederDependencies</c> →
/// <see cref="SlicerModuleExtensions.AddSlicerCalibrationProfileRepositories"/> →
/// <see cref="SlicerModuleExtensions.EnsureSlicerDatabaseRegistered"/>), and
/// <see cref="ModelStorageResolutionStartup.AddModelStorageResolution"/> (#2179), which also
/// reaches <see cref="SlicerModuleExtensions.EnsureSlicerDatabaseRegistered"/>. Neither of those
/// startup methods exists for calibration worker health, so
/// <c>CalibrationCapabilityService.GetWorkerHealthAsync</c>'s
/// <c>_serviceProvider.GetService&lt;IDbContextFactory&lt;SlicerDbContext&gt;&gt;()</c> resolution
/// depended entirely on wiring it has nothing to do with, and would have silently broken (worker
/// health always reported <c>Unavailable</c>) had either of those unrelated startup paths ever
/// been removed or reshaped without anyone noticing the incidental coupling. This class makes the
/// dependency explicit, named, and independently testable, following the same pattern
/// <see cref="ModelStorageResolutionStartup"/> itself established for
/// <see cref="Farm.Slicer.Module.Services.IModelStorageResolver"/> (#2179).
/// </para>
/// <para>
/// <c>Farm.Web.Api.Startup.MoonrakerEmulatorSeederDependenciesStartup.AddMoonrakerEmulatorSeederDependencies</c>
/// still calls this method too, and <see cref="ModelStorageResolutionStartup.AddModelStorageResolution"/>
/// also reaches the same underlying registration — so all three callers share the same single
/// source of truth for "does this split host have a
/// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/> connection" rather than each
/// independently re-deriving it — see
/// <see cref="SlicerModuleExtensions.EnsureSlicerDatabaseRegistered"/>'s own idempotency guard,
/// which makes calling it from multiple entry points safe.
/// </para>
/// </remarks>
public static class CalibrationWorkerHealthDependenciesStartup
{
    /// <summary>
    /// Registers <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> of
    /// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/> for this host when it runs as a split
    /// or microservices deployment, so calibration worker-health capability detection can query
    /// slicer-worker heartbeats regardless of which other startup wiring this host also runs.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCalibrationWorkerHealthDependencies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!CalibrationProfileResolutionStartup.IsSplitDeployment(configuration))
        {
            // Monolith hosts already have IDbContextFactory<SlicerDbContext> via AddSlicerModule.
            return services;
        }

        _ = services.EnsureSlicerDatabaseRegistered(configuration);

        return services;
    }
}
