using System.Reflection;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Guards the binding service-boundary decision from #1613 §5: <c>src/api</c> must reach
/// machine-profile data exclusively through <c>ICalibrationProfileResolver</c>, with zero
/// compile-time or runtime dependency on the OrcaSlicer worker or its profile-cache types
/// (#1614 AC-5, test plan item 5).
/// </summary>
/// <remarks>
/// Anchored on <c>Program</c> (declared in <c>src/api/Program.cs</c>) rather than a
/// calibration type, because as of Phase 10 (#2038) the calibration services moved into
/// <c>Farm.Modules.Calibration</c> — a type from that assembly would silently check the
/// wrong closure. This test specifically verifies the host assembly (<c>Farm.Web.Api</c>),
/// which still project-references <c>Farm.Modules.Calibration</c> (and therefore, since
/// #2038, transitively references <c>Farm.Slicer.Module</c> as well); the equivalent guard
/// for the module assembly itself lives in
/// <c>Farm.Modules.Calibration.Tests.Calibration.CalibrationProfileServiceBoundaryTests</c>.
/// </remarks>
public sealed class FarmWebApiHostAssemblyBoundaryTests
{
    /// <summary>
    /// Assembly names that must never appear in <c>Farm.Web.Api</c>'s referenced-assembly
    /// closure. <c>Farm.OrcaSlicer.Worker</c> hosts <c>ProfileCacheDb</c>; <c>Farm.Slicer.Worker.Core</c>
    /// (the <c>worker-shared</c> project) hosts <c>ISlicerProfilesService</c>. <c>Farm.Slicer.ProfileParsing</c>
    /// (#1615 PR-2) is the shared Orca JSON field-extraction library; nothing in this plan
    /// causes <c>src/api</c> to reference it directly (typed facts arrive pre-populated on
    /// <c>ResolvedCalibrationProfile</c>), but the guard is extended defensively per the issue's
    /// test plan.
    /// </summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "Farm.OrcaSlicer.Worker",
        "Farm.Slicer.Worker.Core",
        "Farm.Slicer.ProfileParsing",
    ];

    [Fact]
    public void FarmWebApiAssembly_DoesNotReferenceOrcaSlicerWorkerOrWorkerSharedAssemblies()
    {
        Assembly apiAssembly = typeof(Program).Assembly;

        IEnumerable<string> referencedAssemblyNames = apiAssembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty);

        _ = referencedAssemblyNames.Should().NotContain(
            name => ForbiddenAssemblyNames.Contains(name, StringComparer.Ordinal),
            "src/api must reach machine-profile data exclusively through " +
            "ICalibrationProfileResolver (#1613 §5), never a direct assembly dependency on " +
            "the OrcaSlicer worker or its profile-cache types");
    }

    [Fact]
    public void FarmWebApiAssembly_HasNoLoadableSlicerProfilesServiceOrProfileCacheDbType()
    {
        Assembly apiAssembly = typeof(Program).Assembly;

        IEnumerable<Type> loadedTypes = apiAssembly.GetTypes();

        _ = loadedTypes.Should().NotContain(type =>
            type.FullName == "Farm.Slicer.Worker.Core.ISlicerProfilesService" ||
            type.FullName == "Farm.OrcaSlicer.Worker.Services.ProfileCacheDb",
            "the calibration context pipeline must not gain a direct type dependency on " +
            "ISlicerProfilesService or ProfileCacheDb");
    }
}
