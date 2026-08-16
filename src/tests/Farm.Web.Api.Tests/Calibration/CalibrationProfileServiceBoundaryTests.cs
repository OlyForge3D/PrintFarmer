using System.Reflection;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Guards the binding service-boundary decision from #1613 §5: <c>src/api</c> must reach
/// machine-profile data exclusively through <c>ICalibrationProfileResolver</c>, with zero
/// compile-time or runtime dependency on the OrcaSlicer worker or its profile-cache types
/// (#1614 AC-5, test plan item 5).
/// </summary>
public sealed class CalibrationProfileServiceBoundaryTests
{
    /// <summary>
    /// Assembly names that must never appear in <c>Farm.Web.Api</c>'s referenced-assembly
    /// closure. <c>Farm.OrcaSlicer.Worker</c> hosts <c>ProfileCacheDb</c>; <c>Farm.Slicer.Worker.Core</c>
    /// (the <c>worker-shared</c> project) hosts <c>ISlicerProfilesService</c>.
    /// </summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "Farm.OrcaSlicer.Worker",
        "Farm.Slicer.Worker.Core",
    ];

    [Fact]
    public void FarmWebApiAssembly_DoesNotReferenceOrcaSlicerWorkerOrWorkerSharedAssemblies()
    {
        Assembly apiAssembly = typeof(PrinterCalibrationContextService).Assembly;

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
        Assembly apiAssembly = typeof(PrinterCalibrationContextService).Assembly;

        IEnumerable<Type> loadedTypes = apiAssembly.GetTypes();

        _ = loadedTypes.Should().NotContain(type =>
            type.FullName == "Farm.Slicer.Worker.Core.ISlicerProfilesService" ||
            type.FullName == "Farm.OrcaSlicer.Worker.Services.ProfileCacheDb",
            "the calibration eligibility pipeline must not gain a direct type dependency on " +
            "ISlicerProfilesService or ProfileCacheDb");
    }
}
