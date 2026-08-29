using Farm.Modules.Calibration.Startup;
using Farm.Slicer.Module;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Ensures <see cref="Farm.Web.Api.Services.Startup.MoonrakerEmulatorSeeder"/> can always resolve
/// its calibration profile repositories, regardless of deployment topology (#1858).
/// </summary>
/// <remarks>
/// <para>
/// <c>MoonrakerEmulatorSeeder</c> runs as a hosted service on the API host in every deployment
/// topology — monolith, split, and microservices — because it seeds/resets printer records the
/// API itself owns. It resolves <c>IMachineProfileRepository</c>,
/// <c>IProcessProfileRepository</c>, and <c>IFilamentProfileRepository</c> from its DI scope.
/// </para>
/// <para>
/// Monolith hosts load the slicer module in process, so <c>AddSlicerModule</c> already registers
/// these repositories and this class does nothing.
/// </para>
/// <para>
/// Split and microservices hosts deliberately do not load the slicer module — it runs in a
/// separate slicer-host process — which previously left these three repositories unregistered on
/// the API host. The daily-validation stack documented for this issue is exactly this topology
/// (<c>DEPLOYMENT_MODE=microservices</c>), so
/// <c>POST /api/test/moonraker-emulator/reset</c> always threw resolving them, turning every
/// Playwright emulator fixture reset into an unconditional 500 before any UI assertion ran. Here
/// the API registers just those three repositories (backed by their own
/// <c>SlicerDbContext</c> connection to the same physical database the slicer-host already
/// shares) rather than adding a slicer-host HTTP endpoint for what is only a handful of
/// deterministic system profiles behind an explicitly opt-in, disabled-by-default validation
/// feature.
/// </para>
/// <para>
/// This method also calls
/// <see cref="Farm.Modules.Calibration.Startup.CalibrationWorkerHealthDependenciesStartup.AddCalibrationWorkerHealthDependencies"/>
/// (#2178), which registers the same
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> of
/// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/> that
/// <see cref="SlicerModuleExtensions.AddSlicerCalibrationProfileRepositories"/> registers below,
/// via the shared <see cref="SlicerModuleExtensions.EnsureSlicerDatabaseRegistered"/> guard. That
/// registration is what
/// <c>CalibrationCapabilityService.GetWorkerHealthAsync</c> depends on to report calibration
/// worker health on split/microservices hosts, and exists independently of this Moonraker-seeder
/// wiring — <c>Farm.Modules.Calibration.CalibrationApiModule.ConfigureServices</c> also calls it
/// unconditionally, alongside
/// <see cref="Farm.Modules.Calibration.Startup.ModelStorageResolutionStartup.AddModelStorageResolution"/>,
/// which reaches the same underlying registration too (#2179). All three callers therefore share
/// one source of truth for "does this split host have a
/// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/> connection" instead of independently
/// re-deriving it, so none of the registrations can drift apart.
/// </para>
/// </remarks>
public static class MoonrakerEmulatorSeederDependenciesStartup
{
    /// <summary>
    /// Registers the calibration profile repositories
    /// <see cref="Farm.Web.Api.Services.Startup.MoonrakerEmulatorSeeder"/> needs when this host
    /// runs as a split or microservices deployment.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMoonrakerEmulatorSeederDependencies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!CalibrationProfileResolutionStartup.IsSplitDeployment(configuration))
        {
            // Monolith hosts already have these repositories via AddSlicerModule.
            return services;
        }

        // Shared source of truth for IDbContextFactory<SlicerDbContext> on split hosts — see the
        // class remarks above and CalibrationWorkerHealthDependenciesStartup (#2178). Calling it
        // here does not duplicate anything AddSlicerCalibrationProfileRepositories itself would
        // register: EnsureSlicerDatabaseRegistered no-ops once SlicerDbContext is present.
        _ = services.AddCalibrationWorkerHealthDependencies(configuration);

        _ = services.AddSlicerCalibrationProfileRepositories(configuration);

        return services;
    }
}
