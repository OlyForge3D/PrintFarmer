using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Devices;

/// <summary>
/// Vertical-slice module for device integrations (issue #2043, epic #2019). Owns the OctoPrint
/// API-key authentication service (<see cref="Farm.Web.Api.Services.OctoPrint.IOctoPrintAuthService"/>),
/// the NFC controllers (<see cref="Farm.Web.Api.Controllers.NfcController"/>,
/// <see cref="Farm.Web.Api.Controllers.NfcDevicesController"/>), the camera controllers
/// (<see cref="Farm.Web.Api.Controllers.CamerasController"/>,
/// <see cref="Farm.Web.Api.Controllers.CameraSnapshotsController"/>), and the admin Home
/// Assistant controller (<see cref="Farm.Web.Api.Controllers.Admin.AdminHomeAssistantController"/>).
/// Phase 15 of the Farm.Web.Api decomposition epic -- see docs/MODULE_MIGRATION_PATTERN.md.
/// </summary>
public sealed class DevicesApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Devices";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // OctoPrint API-key authentication service. OctoPrintSettings itself remains bound in
        // FeatureServicesStartup because Farm.Web.Api.Controllers.OctoPrintCompatController and
        // UserApiKeysController (which are not part of this module) also depend on it.
        _ = services.AddScoped<Farm.Web.Api.Services.OctoPrint.IOctoPrintAuthService, Farm.Web.Api.Services.OctoPrint.OctoPrintAuthService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints -- all Devices controllers are attribute-routed and
        // discovered via the ApplicationPart added during module discovery.
    }
}
