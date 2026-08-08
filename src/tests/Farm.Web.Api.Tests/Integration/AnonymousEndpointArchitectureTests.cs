using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Integration;

[Collection(IntegrationTestCollection.Name)]
public sealed class AnonymousEndpointArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string> ReviewedAnonymousActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Farm.Web.Api.Controllers.AuthController.LoginAsync [api/auth/login]"] =
                "POST /api/auth/login - establishes the user session.",
            ["Farm.Web.Api.Controllers.AuthController.RegisterAsync [api/auth/register]"] =
                "POST /api/auth/register - creates an account before a user can authenticate.",
            ["Farm.Web.Api.Controllers.AuthController.ExchangeApiKeyAsync [api/auth/api-key/exchange]"] =
                "POST /api/auth/api-key/exchange - exchanges the supplied API-key credential for a JWT.",
            ["Farm.Web.Api.Controllers.AuthController.ForgotPasswordAsync [api/auth/forgot-password]"] =
                "POST /api/auth/forgot-password - begins account recovery for a user who cannot authenticate.",
            ["Farm.Web.Api.Controllers.AuthController.ResetPasswordAsync [api/auth/reset-password]"] =
                "POST /api/auth/reset-password - uses a single-use recovery token as the credential.",
            ["Farm.Web.Api.Controllers.AuthController.ConfirmEmailAsync [api/auth/confirm-email]"] =
                "POST /api/auth/confirm-email - uses the issued confirmation token before login.",
            ["Farm.Web.Api.Controllers.AuthController.PasskeyLoginBeginAsync [api/auth/passkey/login/begin]"] =
                "POST /api/auth/passkey/login/begin - produces options needed to start passkey authentication.",
            ["Farm.Web.Api.Controllers.AuthController.PasskeyLoginCompleteAsync [api/auth/passkey/login/complete]"] =
                "POST /api/auth/passkey/login/complete - verifies the signed passkey assertion.",
            ["Farm.Web.Api.Controllers.SetupController.GetSetupStatusAsync [api/setup/status]"] =
                "GET /api/setup/status - reports whether the installation has an account yet.",
            ["Farm.Web.Api.Controllers.SetupController.GetBootstrapAsync [api/setup/bootstrap]"] =
                "GET /api/setup/bootstrap - exposes only the Spoolman base URL while first-run setup is required.",
            ["Farm.Web.Api.Controllers.SetupController.CreateInitialAdminAsync [api/setup/initial-admin]"] =
                "POST /api/setup/initial-admin - creates the installation's first authenticated account.",
            ["Farm.Web.Api.Controllers.SetupController.GetConfigurationOptions [api/setup/config-options]"] =
                "GET /api/setup/config-options - supplies first-run options before an account exists.",
            ["Farm.Web.Api.Controllers.SystemCapabilitiesController.GetCapabilitiesAsync [api/system/capabilities]"] =
                "GET /api/system/capabilities - supplies non-sensitive login and setup compatibility flags.",
            ["Farm.Web.Api.Controllers.SchemaHealthController.SchemaReadyAsync [api/schema-health/ready]"] =
                "GET /api/schema-health/ready - supports credential-free deployment readiness probes.",
            ["Farm.Web.Api.Controllers.FilaManController.GetPrintersAsync [api/filaman/printers]"] =
                "GET /api/filaman/printers - supplies minimal selector metadata to unprovisioned firmware.",
            ["Farm.Web.Api.Controllers.OctoPrintCompatController.UploadFileAsync [api/files/local]"] =
                "POST /api/files/local - requires either an authenticated user or a valid X-Api-Key.",
            ["Farm.Web.Api.Controllers.OctoPrintCompatController.GetVersion [api/version]"] =
                "GET /api/version - exposes non-sensitive compatibility metadata before key configuration.",
            ["Farm.Web.Api.Controllers.OctoPrintCompatController.GetServer [api/server]"] =
                "GET /api/server - exposes non-sensitive compatibility status before key configuration.",
            ["Farm.Web.Api.Controllers.InternalDiscoveryEventsController.ProgressAsync [api/internal/discovery/events/progress]"] =
                "POST /api/internal/discovery/events/progress - authenticates discovery agents with the shared discovery key.",
            ["Farm.Web.Api.Controllers.InternalDiscoveryEventsController.PrinterFoundAsync [api/internal/discovery/events/printer-found]"] =
                "POST /api/internal/discovery/events/printer-found - authenticates discovery agents with the shared discovery key.",
            ["Farm.Web.Api.Controllers.InternalDiscoveryEventsController.CompletedAsync [api/internal/discovery/events/completed]"] =
                "POST /api/internal/discovery/events/completed - authenticates discovery agents with the shared discovery key.",
            ["Farm.Web.Api.Controllers.UnifiedSettingsController.GetSettingsByKeyName [api/settings/{keyName}]"] =
                "GET /api/settings/{keyName} - exposes only the discovery agent's fail-closed section allowlist.",
            ["Farm.Web.Api.Controllers.UnifiedSettingsController.SendHeartbeat [api/settings/{keyName}/heartbeat]"] =
                "POST /api/settings/{keyName}/heartbeat - accepts only the discovery agent heartbeat section.",
            ["Farm.Web.Api.Controllers.UsersController.CheckAvailabilityAsync [api/users/availability]"] =
                "GET /api/users/availability - validates registration identifiers before account creation.",
            ["Farm.Web.Api.Controllers.MonitoringController.VerifySessionAsync [api/monitoring/verify]"] =
                "GET /api/monitoring/verify - verifies the monitoring cookie presented by a reverse proxy.",
            ["Farm.Web.Api.Controllers.NfcDevicesController.HeartbeatAsync [api/nfc-devices/heartbeat]"] =
                "POST /api/nfc-devices/heartbeat - lets unprovisioned NFC firmware announce itself (claim-only, creates a pending device); already-approved devices must still present a valid X-Nfc-Device-Token.",
            ["Farm.Web.Api.Controllers.NfcDevicesController.ScanEventAsync [api/nfc-devices/scan]"] =
                "POST /api/nfc-devices/scan - requires an approved device presenting a valid X-Nfc-Device-Token header, enforced in the service rather than via [Authorize] since firmware has no user JWT.",
            ["Farm.Web.Api.Controllers.SystemSourceController.GetSource [api/system/source]"] =
                "GET /api/system/source - keeps corresponding-source availability public to every recipient.",
            ["Farm.Web.Api.Controllers.GcodeFilesController.GetGcodeThumbnailAsync [api/gcode-files/thumbnail/{id:guid}]"] =
                "GET /api/gcode-files/thumbnail/{id} - supports print-preview image elements without bearer headers.",
            ["Farm.Slicer.Module.Api.Controllers.Model3DFilesController.GetModelThumbnailAsync [api/3d-models/thumbnail/{id:guid}]"] =
                "GET /api/3d-models/thumbnail/{id} - supports model-card image elements without bearer headers.",
            ["Farm.Slicer.Module.Api.Controllers.SlicersController.ListAsync [api/slicers]"] =
                "GET /api/slicers - authenticates slicer hosts with their slicer API key.",
            ["Farm.Slicer.Module.Api.Controllers.SlicersController.RegisterAsync [api/slicers/register]"] =
                "POST /api/slicers/register - authenticates new slicer hosts with the registration key.",
            ["Farm.Slicer.Module.Api.Controllers.SlicersController.GetAsync [api/slicers/{id}]"] =
                "GET /api/slicers/{id} - authenticates the slicer host with its service API key.",
            ["Farm.Slicer.Module.Api.Controllers.SlicersController.HeartbeatAsync [api/slicers/{id}/heartbeat]"] =
                "POST /api/slicers/{id}/heartbeat - authenticates the slicer host with its service API key.",
            ["Farm.Slicer.Module.Api.Controllers.SlicersController.DeregisterAsync [api/slicers/{id}/deregister]"] =
                "POST /api/slicers/{id}/deregister - authenticates the slicer host with its service API key.",
            ["Farm.Slicer.Module.Api.Controllers.SlicersController.RotateApiKeyAsync [api/slicers/{id}/rotate-key]"] =
                "POST /api/slicers/{id}/rotate-key - authenticates the slicer host with its current service API key.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.ClaimAsync [api/slice/claim]"] =
                "POST /api/slice/claim - authenticates workers with the registry-issued worker key and identity.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.ReportProgressAsync [api/slice/{id}/progress]"] =
                "POST /api/slice/{id}/progress - authenticates the claimed worker and validates its lease.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.CompleteAsync [api/slice/{id}/complete]"] =
                "POST /api/slice/{id}/complete - authenticates the claimed worker and validates its lease.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.FailAsync [api/slice/{id}/fail]"] =
                "POST /api/slice/{id}/fail - authenticates the claimed worker and validates its lease.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.RenewLeaseAsync [api/slice/{id}/renew-lease]"] =
                "POST /api/slice/{id}/renew-lease - authenticates the claimed worker and validates its lease.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.DownloadWorkerModelAsync [api/slice/{id}/model]"] =
                "GET /api/slice/{id}/model - authenticates the claimed worker before returning model bytes.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.DownloadWorkerModelAsync [api/slice/{id}/models/{modelIndex:int}]"] =
                "GET /api/slice/{id}/models/{modelIndex} - authenticates the claimed worker before returning model bytes.",
            ["Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.UploadWorkerArtifactAsync [api/slice/{id}/artifacts]"] =
                "POST /api/slice/{id}/artifacts - authenticates the claimed worker before accepting output.",
        };

    [Fact]
    public void AnonymousControllerActions_MatchReviewedAllowlist()
    {
        using var factory = new CustomWebApplicationFactory(
            new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });

        IActionDescriptorCollectionProvider actionProvider =
            factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>();
        ControllerActionDescriptor[] actions = actionProvider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToArray();

        string[] controllerWideAnonymousMetadata = actions
            .Where(action => action.ControllerTypeInfo.IsDefined(typeof(AllowAnonymousAttribute), inherit: true))
            .Select(action => action.ControllerTypeInfo.FullName ?? action.ControllerName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        controllerWideAnonymousMetadata.Should().BeEmpty(
            "anonymous access must be reviewed and documented on each individual controller action");

        string[] actual = actions
            .Where(action => action.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            .Select(GetActionId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = ReviewedAnonymousActions.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.Should().OnlyHaveUniqueItems();
        actual.Should().Equal(
            expected,
            "adding or removing anonymous controller actions requires a deliberate review of this allowlist");
        ReviewedAnonymousActions.Values.Should().OnlyContain(
            rationale => !string.IsNullOrWhiteSpace(rationale),
            "every reviewed anonymous action must explain why it is public");
    }

    private static string GetActionId(ControllerActionDescriptor action) =>
        $"{action.ControllerTypeInfo.FullName}.{action.MethodInfo.Name} [{action.AttributeRouteInfo?.Template}]";
}
