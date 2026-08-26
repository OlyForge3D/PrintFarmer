using Farm.Modules.Abstractions;
using Farm.Web.Api.Services.Gcode;
using Farm.Web.Api.Services.Gcode.Safety;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Gcode;

/// <summary>
/// Vertical-slice module for gcode file management (issue #2039, epic #2019). Owns the
/// <see cref="Farm.Web.Api.Controllers.GcodeFilesController"/>,
/// <see cref="Farm.Web.Api.Controllers.GcodeLibraryController"/>,
/// <see cref="Farm.Web.Api.Controllers.GcodePromotionsController"/>,
/// <see cref="Farm.Web.Api.Controllers.GcodeHarvestController"/>, and
/// <see cref="Farm.Web.Api.Controllers.GcodeHarvestDiagnosticsController"/> controllers, the
/// gcode library/upload/safety-validation services, artifact-to-gcode promotion and its
/// reconciler, and the file-consistency audit background service. Phase 11 of the Farm.Web.Api
/// decomposition epic (see docs/MODULE_MIGRATION_PATTERN.md). Namespaces are intentionally
/// unchanged from their prior Farm.Web.Api location (move-first-rename-last). Requires the
/// slicer module (<c>AddSlicerModule</c>) for artifact routing and calibration promotion --
/// consistent with the slicer-on <c>HostFixture</c> sub-fixture used by Phase 4.
/// </summary>
public sealed class GcodeApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Gcode";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Gcode library service: queries, retrieval, deletion of harvested gcode files.
        _ = services.AddScoped<IGcodeFilesService, GcodeFilesService>();

        // Gcode safety validation (thermal/geometry sanity checks ahead of print dispatch).
        services.TryAddSingleton<IGcodeSafetyValidator, GcodeSafetyValidator>();

        // Infrastructure-owned processing seam: bridges the infra IGcodeFileProcessingService
        // abstraction to this module's concrete IGcodeFilesService so callers outside this
        // module (e.g. harvest completion) can process gcode without a compile-time reference
        // into Farm.Modules.Gcode.
        _ = services.AddScoped<Farm.Infrastructure.Services.Gcode.IGcodeFileProcessingService>(sp =>
            (Farm.Infrastructure.Services.Gcode.IGcodeFileProcessingService)sp.GetRequiredService<IGcodeFilesService>());

        // Harvest completion event broadcasting over SignalR.
        _ = services.AddScoped<Farm.Infrastructure.Services.Gcode.IHarvestEventBroadcaster, SignalRHarvestEventBroadcaster>();

        // Gcode upload settings, backed by the persisted ISettingsService store.
        _ = services.AddScoped<Farm.Infrastructure.Services.Interfaces.IGcodeUploadSettings, PersistedGcodeUploadSettingsAdapter>();

        // Artifact -> GcodeFile promotion: scoped promoter plus the reconciler that resolves the
        // unknown outcomes a crash or a transient outage can leave between the slicer and core
        // contexts. IGcodeArtifactPromoter itself lives in Farm.Modules.Calibration (Phase 10,
        // #2038) because Calibration's slice-promotion saga is its primary consumer.
        _ = services.AddSingleton<GcodePromotionReconcilerState>();
        _ = services.AddScoped<IGcodeArtifactPromoter, GcodeArtifactPromoter>();
        _ = services.AddHostedService<GcodePromotionReconciliationService>();

        // File consistency audit: runs hourly to detect orphaned/missing/corrupted files. Uses
        // IFileAuditRepository from Farm.Slicer.Module.Repositories (registered by AddSlicerModule
        // -- this module requires the slicer module to be enabled).
        _ = services.AddHostedService(sp =>
        {
            IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            IConfiguration config = sp.GetRequiredService<IConfiguration>();
            string modelStoragePath = config["ModelStorage:Path"] ?? Path.Join(Directory.GetCurrentDirectory(), "models");
            string gcodeStoragePath = config["GcodeStorage:Path"] ?? Path.Join(Directory.GetCurrentDirectory(), "gcode-library");
            return new Farm.Infrastructure.Services.FileManagement.FileConsistencyAuditService(
                scopeFactory,
                loggerFactory.CreateLogger<Farm.Infrastructure.Services.FileManagement.FileConsistencyAuditService>(),
                modelStoragePath,
                gcodeStoragePath);
        });
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints -- the gcode controllers are attribute-routed and discovered
        // via the ApplicationPart added during module discovery.
    }
}
