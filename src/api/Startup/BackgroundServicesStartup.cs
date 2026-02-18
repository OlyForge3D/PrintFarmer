using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Artifacts;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures background services (hosted services) for workers, maintenance, and cleanup.
/// </summary>
public static class BackgroundServicesStartup
{
    /// <summary>
    /// Adds PrintFarmer background services (maintenance, history seeding, cleanup).
    /// Slicer-specific hosted services are now registered by <c>AddSlicerModule()</c>
    /// in <c>Farm.Slicer.Module</c>.
    /// </summary>
    public static IServiceCollection AddPrintFarmerBackgroundServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Maintenance Module - Print Statistics Sync Service
        services.Configure<Farm.Web.Api.Services.Maintenance.PrintStatsSyncSettings>(configuration.GetSection(Farm.Web.Api.Services.Maintenance.PrintStatsSyncSettings.SectionName));
        services.AddHostedService<Farm.Web.Api.Services.Maintenance.PrintStatsSyncHostedService>();

        // Maintenance Module - Maintenance Alert Engine
        services.Configure<Farm.Web.Api.Services.Maintenance.MaintenanceAlertSettings>(configuration.GetSection(Farm.Web.Api.Services.Maintenance.MaintenanceAlertSettings.SectionName));
        services.AddHostedService<Farm.Web.Api.Services.Maintenance.MaintenanceAlertHostedService>();

        // Orphaned Job Sync - Runs once on startup to sync jobs stuck in "Printing" status
        // This handles cases where the API restarts while a print completes
        services.AddHostedService<Farm.Web.Api.Services.Startup.OrphanedJobSyncStartupService>();

        // History Seeding - Periodically seeds job history from connected printers
        // This captures jobs dispatched outside of PrintFarmer (e.g., via Mainsail/Fluidd)
        services.Configure<Farm.Web.Api.Services.Workers.HistorySeedingSettings>(configuration.GetSection(Farm.Web.Api.Services.Workers.HistorySeedingSettings.SectionName));
        services.AddHostedService<Farm.Web.Api.Services.Workers.HistorySeedingBackgroundService>();

        // Register asset service for OrcaSlicer printer images and bed textures
        services.AddSingleton<IAssetService, AssetService>();

        // Register file consistency audit background service
        // Runs hourly to detect orphaned/missing/corrupted files
        services.AddHostedService(sp =>
        {
            IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            IUnifiedLoggingService logger = sp.GetRequiredService<IUnifiedLoggingService>();
            IConfiguration config = sp.GetRequiredService<IConfiguration>();
            string modelStoragePath = config["ModelStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "models");
            string gcodeStoragePath = config["GcodeStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "gcode-library");
            return new Farm.Web.Api.Services.FileManagement.FileConsistencyAuditService(
                scopeFactory,
                logger,
                modelStoragePath,
                gcodeStoragePath);
        });

        // Circuit breaker for worker failure tracking (slicer service implementation).
        // TODO: Move to Farm.Slicer.Module once WorkerCircuitBreakerService is extracted (bead PFarm1-2ni.1.2).
        if (configuration.GetValue("Slicer:Enabled", true))
        {
            services.AddSingleton<Farm.Slicer.Module.Services.IWorkerCircuitBreakerService, Farm.Web.Api.Services.Workers.WorkerCircuitBreakerService>();
        }

        return services;
    }
}
