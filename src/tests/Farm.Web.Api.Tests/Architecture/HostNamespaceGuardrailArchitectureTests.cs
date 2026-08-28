using System.Reflection;

namespace Farm.Web.Api.Tests.Architecture;

/// <summary>
/// Guardrail for issue #2048 (Phase 20, the final phase of the module-decomposition epic
/// #2019): asserts <c>Farm.Web.Api</c> — the host assembly — contains no type under a
/// <c>Controllers</c> or <c>Services</c> namespace that isn't explicitly on the allowlist
/// below. The epic spent 19 phases moving 11 feature-area modules out of the host into
/// their own <c>Farm.Modules.*</c> assemblies specifically to keep the host thin; without a
/// guardrail, new controllers/services can silently re-accumulate here exactly the way the
/// original 255-file / 61,068-LOC monolith did (see the epic's own risk analysis).
/// </summary>
/// <remarks>
/// The allowlist reflects the *actual* host-scoped surface as of Phase 20, not the epic's
/// original phase-planning snapshot: some controllers that snapshot listed as staying
/// (<c>BackgroundServicesController</c>, <c>SignalRTestController</c>) have since moved into
/// <c>Farm.Modules.Observability</c>, and several controllers not named in that snapshot
/// (<c>AssetsController</c>, <c>CalibrationCapabilitiesController</c>,
/// <c>InternalSlicerHostLookupsController</c>, <c>LibrarySyncController</c>,
/// <c>OctoPrintCompatController</c>, <c>PredictionController</c>,
/// <c>PrintProjectTemplatesController</c>, <c>ReportExportController</c>,
/// <c>SystemLogsController</c>) are genuinely host-scoped and remain here. Per this phase's
/// explicit non-goal ("no further module extraction — cleanup only"), the guardrail
/// encodes today's real boundary rather than an aspirational one that would immediately
/// fail or fail to protect anything.
/// </remarks>
public sealed class HostNamespaceGuardrailArchitectureTests
{
    /// <summary>
    /// Full type names explicitly permitted under a <c>Farm.Web.Api.Controllers</c> or
    /// <c>Farm.Web.Api.Services</c> namespace (or a sub-namespace of either) in the host
    /// assembly. Add an entry here only when a new type is a deliberate, reviewed addition
    /// to the host's own scope — never to silently let a misplaced controller or service
    /// back in.
    /// </summary>
    private static readonly HashSet<string> AllowedTypeNames = new(StringComparer.Ordinal)
    {
        // Controllers/**
        "Farm.Web.Api.Controllers.AssetsController",
        "Farm.Web.Api.Controllers.CalibrationCapabilitiesController",
        "Farm.Web.Api.Controllers.InternalSlicerHostLookupsController",
        "Farm.Web.Api.Controllers.LibrarySyncController",
        "Farm.Web.Api.Controllers.MoonrakerEmulatorControlController",
        "Farm.Web.Api.Controllers.OctoPrintCompatController",
        "Farm.Web.Api.Controllers.PredictionController",
        "Farm.Web.Api.Controllers.PrintProjectTemplatesController",
        "Farm.Web.Api.Controllers.RecordCompletionRequest",
        "Farm.Web.Api.Controllers.ReportExportController",
        "Farm.Web.Api.Controllers.SchemaHealthController",
        "Farm.Web.Api.Controllers.SetupController",
        "Farm.Web.Api.Controllers.SystemCapabilitiesController",
        "Farm.Web.Api.Controllers.SystemInfoController",
        "Farm.Web.Api.Controllers.SystemLogsController",
        "Farm.Web.Api.Controllers.SystemSourceController",
        "Farm.Web.Api.Controllers.Requests.UploadGcodeRequest",

        // Services/Startup/**, Services/StorageManagement/**, Services/SlicerHost/**
        "Farm.Web.Api.Services.SlicerHost.SlicerHostServiceAuthenticator",
        "Farm.Web.Api.Services.StorageManagement.AspNetCorePathProvider",
        "Farm.Web.Api.Services.Startup.DatabaseInitializer",
        "Farm.Web.Api.Services.Startup.GracefulShutdownService",
        "Farm.Web.Api.Services.Startup.MoonrakerEmulatorPrinterSeed",
        "Farm.Web.Api.Services.Startup.MoonrakerEmulatorSeedSettings",
        "Farm.Web.Api.Services.Startup.MoonrakerEmulatorSeeder",
        "Farm.Web.Api.Services.Startup.OrphanedJobSyncStartupService",
    };

    private static bool IsControllersOrServicesNamespace(string? ns)
    {
        if (string.IsNullOrEmpty(ns))
        {
            return false;
        }

        return ns == "Farm.Web.Api.Controllers"
            || ns.StartsWith("Farm.Web.Api.Controllers.", StringComparison.Ordinal)
            || ns == "Farm.Web.Api.Services"
            || ns.StartsWith("Farm.Web.Api.Services.", StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Presubmit: Farm.Web.Api has no Controllers/Services type outside the allowed host set")]
    public void HostAssembly_HasNoDisallowedControllerOrServiceType()
    {
        Assembly apiAssembly = typeof(Program).Assembly;

        List<string> offenders = apiAssembly
            .GetTypes()
            .Where(t => !t.IsNested && IsControllersOrServicesNamespace(t.Namespace))
            .Select(t => t.FullName ?? t.Name)
            .Where(fullName => !AllowedTypeNames.Contains(fullName))
            .OrderBy(fullName => fullName, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The following types live under a Controllers/Services namespace in the " +
            "Farm.Web.Api host assembly but are not on the allowlist (issue #2048 / epic " +
            "#2019 Phase 20 guardrail). Move genuinely feature-scoped code into its own " +
            "Farm.Modules.* assembly, or add a justified entry to " +
            $"{nameof(AllowedTypeNames)} if it is truly host-scoped. Offenders: " +
            string.Join(", ", offenders));
    }
}
