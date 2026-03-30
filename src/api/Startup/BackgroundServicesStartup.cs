using Farm.Infrastructure.Services.Assets;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Web.Api.Services;
using Microsoft.Extensions.Logging;

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
        services.Configure<Farm.Infrastructure.Services.Maintenance.PrintStatsSyncSettings>(configuration.GetSection(Farm.Infrastructure.Services.Maintenance.PrintStatsSyncSettings.SectionName));
        services.AddHostedService<Farm.Web.Api.Services.Maintenance.PrintStatsSyncHostedService>();

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
        services.Configure<Farm.Web.Api.Services.Workers.HistorySeedingSettings>(configuration.GetSection(Farm.Web.Api.Services.Workers.HistorySeedingSettings.SectionName));
        services.AddHostedService<Farm.Web.Api.Services.Workers.HistorySeedingBackgroundService>();

        // Register asset service for OrcaSlicer printer images and bed textures
        services.AddSingleton<IAssetService, AssetService>();

        // Register file consistency audit background service
        // Runs hourly to detect orphaned/missing/corrupted files
        // Uses IFileAuditRepository from Farm.Slicer.Module.Repositories (registered by slicer integration)
        services.AddHostedService(sp =>
        {
            IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            IConfiguration config = sp.GetRequiredService<IConfiguration>();
            string modelStoragePath = config["ModelStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "models");
            string gcodeStoragePath = config["GcodeStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "gcode-library");
            return new Farm.Infrastructure.Services.FileManagement.FileConsistencyAuditService(
                scopeFactory,
                loggerFactory.CreateLogger<Farm.Infrastructure.Services.FileManagement.FileConsistencyAuditService>(),
                modelStoragePath,
                gcodeStoragePath);
        });

        return services;
    }
}
