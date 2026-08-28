using Farm.Infrastructure.Services.Assets;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Startup;

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
        services.Configure<MoonrakerEmulatorSeedSettings>(
            configuration.GetSection(MoonrakerEmulatorSeedSettings.SectionName));
        services.AddSingleton<MoonrakerEmulatorSeeder>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<MoonrakerEmulatorSeeder>());

        // Maintenance Module - Print Statistics Sync Service is registered by
        // MaintenanceApiModule.ConfigureServices() -- moved to Farm.Modules.Maintenance
        // (issue #2037).

        // Maintenance Module - Maintenance Alert Engine
        services.Configure<Farm.Infrastructure.Settings.MaintenanceAlertSettings>(configuration.GetSection(Farm.Infrastructure.Settings.MaintenanceAlertSettings.SectionName));
        services.AddHostedService<Farm.Infrastructure.Services.Maintenance.MaintenanceAlertHostedService>();

        // Catalog Module - Catalog Update Detection
        // Periodically checks if any printer's model template has been updated in the catalog
        // and notifies active users so they can apply the latest configuration defaults.
        services.Configure<Farm.Infrastructure.Settings.CatalogUpdateSettings>(configuration.GetSection(Farm.Infrastructure.Settings.CatalogUpdateSettings.SectionName));
        services.AddHostedService<Farm.Infrastructure.Services.Catalog.CatalogUpdateDetectionService>();

        // Orphaned Job Sync - Runs periodically (every 60s) to sync jobs stuck in "Printing" status
        // Catches missed state transitions from direct printer cancellations or WebSocket drops
        services.AddHostedService<Farm.Web.Api.Services.Startup.OrphanedJobSyncStartupService>();

        // History Seeding - Periodically seeds job history from connected printers
        // This captures jobs dispatched outside of PrintFarmer (e.g., via Mainsail/Fluidd)
        services.Configure<Farm.Modules.Observability.Services.Workers.HistorySeedingSettings>(configuration.GetSection(Farm.Modules.Observability.Services.Workers.HistorySeedingSettings.SectionName));
        services.AddHostedService<Farm.Modules.Observability.Services.Workers.HistorySeedingBackgroundService>();

        // Active External Job Sync - faster cadence for non-terminal externally-started jobs
        services.AddHostedService<Farm.Modules.Observability.Services.Workers.ActiveExternalJobSyncBackgroundService>();

        // Register asset service for OrcaSlicer printer images and bed textures
        services.AddSingleton<IAssetService, AssetService>();

        // File consistency audit background service moved to Farm.Modules.Gcode's
        // GcodeApiModule (issue #2039, epic #2019) alongside FileConsistencyAuditService itself.

        // Electricity Module - prune PowerReading rows older than 90 days, runs daily
        services.AddHostedService<Farm.Infrastructure.Services.Electricity.PowerReadingPruneService>();

        // Queue Module - prune QueueDispatchOutbox/QueueDispatchAttempts/QueueOperationAudits
        // rows past their independently configured retention windows (issue #1728).
        services.Configure<Farm.Infrastructure.Services.Queue.QueueRetentionSettings>(
            configuration.GetSection(Farm.Infrastructure.Services.Queue.QueueRetentionSettings.SectionName));
        services.AddHostedService<Farm.Infrastructure.Services.Queue.QueueRetentionPruneService>();

        // Electricity Module - PowerMonitorPollingService is now registered by
        // Farm.Modules.SmartPlug's SmartPlugApiModule (issue #2036, epic #2019).
        return services;
    }
}
