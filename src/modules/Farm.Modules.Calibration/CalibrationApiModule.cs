using Farm.Modules.Abstractions;
using Farm.Modules.Calibration.Startup;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Calibration;

/// <summary>
/// Vertical-slice module for filament calibration (issue #2038, epic #2019). Owns the
/// <see cref="Farm.Modules.Calibration.Controllers.CalibrationProjectsController"/> and
/// <see cref="Farm.Modules.Calibration.Controllers.CalibrationOrchestrationsController"/> controllers, the
/// calibration project/attempt/photo/orchestration services, blob storage, capability
/// negotiation, and the split-deployment profile-resolution startup wiring. Phase 10 of the
/// Farm.Web.Api decomposition epic (see docs/MODULE_MIGRATION_PATTERN.md). Namespaces were
/// renamed from Farm.Web.Api.* to Farm.Modules.Calibration.* by Phase 19 (issue #2047),
/// completing the move-first-rename-last strategy.
/// </summary>
public sealed class CalibrationApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Calibration";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Calibration profile resolution. Monolith hosts keep the local database-backed resolver
        // registered by AddSlicerModule; split/microservices hosts get the authenticated HTTP
        // adapter that reaches the slicer host owning the profile store (otherwise no resolver
        // exists and calibration discovery returns profile_service_unavailable).
        _ = services.AddCalibrationProfileResolution(configuration);

        // Model storage resolution. Monolith hosts keep the local filesystem-backed resolver
        // registered by AddSlicerModule; split/microservices hosts get it registered here too,
        // sharing the same physical database and model-storage volume the API host already has
        // (no HTTP hop needed) — see ModelStorageResolutionStartup and issue #2179.
        _ = services.AddModelStorageResolution(configuration);

        _ = services.AddSingleton(
            new Farm.Infrastructure.PrinterCalibration.CalibrationSlicerCompatibilityPolicy(
                configuration
                    .GetSection(
                        Farm.Infrastructure.PrinterCalibration.CalibrationSlicerCompatibilityPolicy
                            .ConfigurationKey)
                    .Get<string[]>()));

        _ = services.AddScoped<
            Farm.Modules.Calibration.Services.Capabilities.ICalibrationCapabilityService,
            Farm.Modules.Calibration.Services.Capabilities.CalibrationCapabilityService>();

        _ = services.AddOptions<Farm.Modules.Calibration.Services.Calibration.CalibrationBlobStorageOptions>()
            .Bind(configuration.GetSection(
                Farm.Modules.Calibration.Services.Calibration.CalibrationBlobStorageOptions.SectionName))
            .Validate(
                options =>
                    !string.IsNullOrWhiteSpace(options.RootPath) &&
                    options.MaxBytes > 0 &&
                    options.MaxWidth > 0 &&
                    options.MaxHeight > 0 &&
                    options.MaxPixels > 0,
                "Calibration blob storage requires a private root and positive limits.")
            .ValidateOnStart();
        _ = services.AddSingleton<
            Farm.Modules.Calibration.Services.Calibration.ICalibrationBlobStore,
            Farm.Modules.Calibration.Services.Calibration.CalibrationBlobStore>();
        _ = services.AddScoped<
            Farm.Modules.Calibration.Services.Calibration.ICalibrationProjectService,
            Farm.Modules.Calibration.Services.Calibration.CalibrationProjectService>();
        _ = services.AddHostedService<
            Farm.Modules.Calibration.Services.Calibration.CalibrationPhotoDeleteReconciliationService>();

        // Filament-calibration saga: drives the existing CalibrationOrchestration checkpoint
        // through created -> ... -> completed by calling the real
        // SliceJobController/SlicePrintBridgeController HTTP contracts, never by re-implementing
        // their logic. The internal HttpClient's base address is pinned from trusted
        // configuration (never derived from an inbound request's Host/Scheme, which would let a
        // caller redirect this server's own bearer-token bearing calls to an arbitrary host it
        // controls) - matching the same configuration-driven pattern already used for
        // cross-process internal calls (see Farm.Slicer.Host's "MainApi" client).
        _ = services.AddHttpContextAccessor();
        string calibrationSagaInternalApiBaseUrl =
            configuration["Calibration:InternalApiBaseUrl"]
            ?? configuration["MainApi:BaseUrl"]
            ?? "http://localhost:5245";
        _ = services.AddHttpClient(
            Farm.Modules.Calibration.Services.Calibration.InternalApiSliceSubmissionGateway.HttpClientName,
            client => client.BaseAddress = new Uri(calibrationSagaInternalApiBaseUrl.TrimEnd('/') + "/"));
        _ = services.AddScoped<
            Farm.Modules.Calibration.Services.Calibration.ISliceSubmissionGateway,
            Farm.Modules.Calibration.Services.Calibration.InternalApiSliceSubmissionGateway>();
        _ = services.AddScoped<
            Farm.Modules.Calibration.Services.Calibration.IPrintDispatchGateway,
            Farm.Modules.Calibration.Services.Calibration.InternalApiPrintDispatchGateway>();
        _ = services.AddScoped<
            Farm.Modules.Calibration.Services.Calibration.ICalibrationOrchestrationSagaService,
            Farm.Modules.Calibration.Services.Calibration.CalibrationOrchestrationSagaService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints -- CalibrationProjectsController and
        // CalibrationOrchestrationsController are attribute-routed and discovered via the
        // ApplicationPart added during module discovery.
    }
}
