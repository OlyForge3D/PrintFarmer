using Farm.Modules.Abstractions;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Calibration;

/// <summary>
/// Vertical-slice module for filament calibration (issue #2038, epic #2019). Owns the
/// <see cref="Farm.Web.Api.Controllers.CalibrationProjectsController"/> and
/// <see cref="Farm.Web.Api.Controllers.CalibrationOrchestrationsController"/> controllers, the
/// calibration project/attempt/photo/orchestration services, blob storage, capability
/// negotiation, and the split-deployment profile-resolution startup wiring. Phase 10 of the
/// Farm.Web.Api decomposition epic (see docs/MODULE_MIGRATION_PATTERN.md). Namespaces are
/// intentionally unchanged from their prior Farm.Web.Api location (move-first-rename-last).
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

        _ = services.AddSingleton(
            new Farm.Infrastructure.PrinterCalibration.CalibrationSlicerCompatibilityPolicy(
                configuration
                    .GetSection(
                        Farm.Infrastructure.PrinterCalibration.CalibrationSlicerCompatibilityPolicy
                            .ConfigurationKey)
                    .Get<string[]>()));

        _ = services.AddScoped<
            Farm.Web.Api.Services.Capabilities.ICalibrationCapabilityService,
            Farm.Web.Api.Services.Capabilities.CalibrationCapabilityService>();

        _ = services.AddOptions<Farm.Web.Api.Services.Calibration.CalibrationBlobStorageOptions>()
            .Bind(configuration.GetSection(
                Farm.Web.Api.Services.Calibration.CalibrationBlobStorageOptions.SectionName))
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
            Farm.Web.Api.Services.Calibration.ICalibrationBlobStore,
            Farm.Web.Api.Services.Calibration.CalibrationBlobStore>();
        _ = services.AddScoped<
            Farm.Web.Api.Services.Calibration.ICalibrationProjectService,
            Farm.Web.Api.Services.Calibration.CalibrationProjectService>();
        _ = services.AddHostedService<
            Farm.Web.Api.Services.Calibration.CalibrationPhotoDeleteReconciliationService>();

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
            Farm.Web.Api.Services.Calibration.InternalApiSliceSubmissionGateway.HttpClientName,
            client => client.BaseAddress = new Uri(calibrationSagaInternalApiBaseUrl.TrimEnd('/') + "/"));
        _ = services.AddScoped<
            Farm.Web.Api.Services.Calibration.ISliceSubmissionGateway,
            Farm.Web.Api.Services.Calibration.InternalApiSliceSubmissionGateway>();
        _ = services.AddScoped<
            Farm.Web.Api.Services.Calibration.IPrintDispatchGateway,
            Farm.Web.Api.Services.Calibration.InternalApiPrintDispatchGateway>();
        _ = services.AddScoped<
            Farm.Web.Api.Services.Calibration.ICalibrationOrchestrationSagaService,
            Farm.Web.Api.Services.Calibration.CalibrationOrchestrationSagaService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints -- CalibrationProjectsController and
        // CalibrationOrchestrationsController are attribute-routed and discovered via the
        // ApplicationPart added during module discovery.
    }
}
