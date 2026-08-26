using System.Reflection;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;

namespace Farm.Modules.Calibration.Tests.Calibration;

/// <summary>
/// Defensive extension of the binding service-boundary decision from #1613 §5 to the
/// <c>Farm.Modules.Calibration</c> module (Phase 10, #2038): the module must reach
/// machine-profile data exclusively through <c>ICalibrationProfileResolver</c>, with zero
/// compile-time or runtime dependency on the OrcaSlicer worker or its profile-cache types
/// (#1614 AC-5, test plan item 5). The original guard against <c>Farm.Web.Api</c> itself
/// stays in <c>Farm.Web.Api.Tests</c> (see
/// <c>FarmWebApiHostAssemblyBoundaryTests</c>) — the host assembly is a distinct
/// compile-time closure from this module and must be checked independently.
/// </summary>
public sealed class CalibrationProfileServiceBoundaryTests
{
    /// <summary>
    /// Assembly names that must never appear in <c>Farm.Modules.Calibration</c>'s
    /// referenced-assembly closure. <c>Farm.OrcaSlicer.Worker</c> hosts <c>ProfileCacheDb</c>;
    /// <c>Farm.Slicer.Worker.Core</c> (the <c>worker-shared</c> project) hosts
    /// <c>ISlicerProfilesService</c>. <c>Farm.Slicer.ProfileParsing</c> (#1615 PR-2) is the
    /// shared Orca JSON field-extraction library; nothing in this plan causes the calibration
    /// module to reference it directly (typed facts arrive pre-populated on
    /// <c>ResolvedCalibrationProfile</c>), but the guard is extended defensively per the
    /// issue's test plan.
    /// </summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "Farm.OrcaSlicer.Worker",
        "Farm.Slicer.Worker.Core",
        "Farm.Slicer.ProfileParsing",
    ];

    [Fact]
    public void CalibrationModuleAssembly_DoesNotReferenceOrcaSlicerWorkerOrWorkerSharedAssemblies()
    {
        Assembly moduleAssembly = typeof(CalibrationProjectService).Assembly;

        IEnumerable<string> referencedAssemblyNames = moduleAssembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty);

        _ = referencedAssemblyNames.Should().NotContain(
            name => ForbiddenAssemblyNames.Contains(name, StringComparer.Ordinal),
            "Farm.Modules.Calibration must reach machine-profile data exclusively through " +
            "ICalibrationProfileResolver (#1613 §5), never a direct assembly dependency on " +
            "the OrcaSlicer worker or its profile-cache types");
    }

    [Fact]
    public void CalibrationModuleAssembly_HasNoLoadableSlicerProfilesServiceOrProfileCacheDbType()
    {
        Assembly moduleAssembly = typeof(CalibrationProjectService).Assembly;

        IEnumerable<Type> loadedTypes = moduleAssembly.GetTypes();

        _ = loadedTypes.Should().NotContain(type =>
            type.FullName == "Farm.Slicer.Worker.Core.ISlicerProfilesService" ||
            type.FullName == "Farm.OrcaSlicer.Worker.Services.ProfileCacheDb",
            "the calibration context pipeline must not gain a direct type dependency on " +
            "ISlicerProfilesService or ProfileCacheDb");
    }
}
