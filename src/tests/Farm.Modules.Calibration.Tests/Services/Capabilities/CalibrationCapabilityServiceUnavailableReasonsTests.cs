using System.Reflection;
using Farm.Infrastructure.Dtos;
using Farm.Modules.Calibration.Services.Capabilities;
using Farm.Modules.Calibration.Services.Gcode;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Modules.Calibration.Tests.Services.Capabilities;

/// <summary>
/// Covers the invariant that <c>BuildUnavailableReasons</c> must diagnose every conjunct of
/// <c>calibrationSlicingOperational</c> (issue #2158). The service computes
/// <c>calibrationSlicingOperational = slicingOperational &amp;&amp; PinnedIdentityCount &gt; 0 &amp;&amp;
/// modelStorageResolvable</c> but, before this fix, the reason-building chain only diagnosed the
/// first conjunct: when <c>slicingOperational</c> was <see langword="true"/> but the other two
/// were not, <c>unavailableReasons</c> stayed empty even though the feature was unavailable.
/// </summary>
public sealed class CalibrationCapabilityServiceUnavailableReasonsTests
{
    private static readonly MethodInfo BuildUnavailableReasonsMethod =
        typeof(CalibrationCapabilityService).GetMethod(
            "BuildUnavailableReasons",
            BindingFlags.NonPublic | BindingFlags.Instance) ??
        throw new MissingMethodException(
            nameof(CalibrationCapabilityService), "BuildUnavailableReasons");

    private static readonly Type WorkerHealthSnapshotType =
        typeof(CalibrationCapabilityService).GetNestedType(
            "WorkerHealthSnapshot", BindingFlags.NonPublic) ??
        throw new MissingMemberException(
            nameof(CalibrationCapabilityService), "WorkerHealthSnapshot");

    private static readonly CalibrationCapabilityService Service = new(
        new ConfigurationBuilder().Build(),
        new ServiceCollection().BuildServiceProvider(),
        NullLogger<CalibrationCapabilityService>.Instance);

    /// <summary>
    /// For every input combination that leaves <c>calibrationSlicingOperational</c> false,
    /// <c>unavailableReasons</c> must contain at least one entry with <c>Feature == "slicing"</c>.
    /// The combinations are constrained so that <c>slicingOperational</c> can only be
    /// <see langword="true"/> when <c>slicingEnabled</c>, <c>workerAuthenticationConfigured</c> and
    /// <c>RegistryAvailable</c> are all <see langword="true"/>, matching how the service actually
    /// derives it, so every generated combination is reachable in production.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void BuildUnavailableReasons_WhenCalibrationSlicingOperationalIsFalse_ReportsSlicingReason(
        bool slicingEnabled,
        bool workerAuthenticationConfigured,
        bool registryAvailable,
        bool slicingOperational,
        int pinnedIdentityCount,
        bool modelStorageResolvable)
    {
        bool calibrationSlicingOperational =
            slicingOperational && pinnedIdentityCount > 0 && modelStorageResolvable;

        IReadOnlyList<CapabilityUnavailableReasonDto> reasons = InvokeBuildUnavailableReasons(
            slicingEnabled,
            workerAuthenticationConfigured,
            slicingOperational,
            registryAvailable,
            pinnedIdentityCount,
            modelStorageResolvable);

        if (!calibrationSlicingOperational)
        {
            _ = reasons.Should().Contain(
                reason => reason.Feature == "slicing",
                because:
                "calibrationSlicingOperational is false (slicingOperational={0}, pinnedIdentityCount={1}, modelStorageResolvable={2}) so at least one slicing reason must be diagnosable",
                slicingOperational,
                pinnedIdentityCount,
                modelStorageResolvable);
        }
    }

    /// <summary>
    /// Control case: when every input is fully operational, no "slicing" unavailable reason is
    /// produced. Without this, the general invariant test above could pass vacuously if the
    /// production code always emitted a spurious slicing reason regardless of input.
    /// </summary>
    [Fact]
    public void BuildUnavailableReasons_WhenFullyOperational_ReportsNoSlicingReason()
    {
        IReadOnlyList<CapabilityUnavailableReasonDto> reasons = InvokeBuildUnavailableReasons(
            slicingEnabled: true,
            workerAuthenticationConfigured: true,
            slicingOperational: true,
            registryAvailable: true,
            pinnedIdentityCount: 1,
            modelStorageResolvable: true);

        _ = reasons.Should().NotContain(reason => reason.Feature == "slicing");
    }

    public static IEnumerable<object[]> AllCombinations()
    {
        foreach (bool slicingEnabled in new[] { false, true })
        {
            foreach (bool workerAuthenticationConfigured in new[] { false, true })
            {
                foreach (bool registryAvailable in new[] { false, true })
                {
                    foreach (bool slicingOperational in new[] { false, true })
                    {
                        // slicingOperational can only be true when its own prerequisites hold;
                        // production never derives it otherwise, so skip unreachable combinations.
                        if (slicingOperational &&
                            !(slicingEnabled && workerAuthenticationConfigured && registryAvailable))
                        {
                            continue;
                        }

                        foreach (int pinnedIdentityCount in new[] { 0, 1 })
                        {
                            foreach (bool modelStorageResolvable in new[] { false, true })
                            {
                                yield return new object[]
                                {
                                    slicingEnabled,
                                    workerAuthenticationConfigured,
                                    registryAvailable,
                                    slicingOperational,
                                    pinnedIdentityCount,
                                    modelStorageResolvable,
                                };
                            }
                        }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<CapabilityUnavailableReasonDto> InvokeBuildUnavailableReasons(
        bool slicingEnabled,
        bool workerAuthenticationConfigured,
        bool slicingOperational,
        bool registryAvailable,
        int pinnedIdentityCount,
        bool modelStorageResolvable)
    {
        object workerHealth = Activator.CreateInstance(
            WorkerHealthSnapshotType,
            registryAvailable,
            slicingOperational ? 1 : 0,
            slicingOperational ? 1 : 0,
            workerAuthenticationConfigured ? 1 : 0,
            pinnedIdentityCount,
            Array.Empty<string>(),
            true)!;

        GcodePromotionCapabilityDto promotionCapability = new()
        {
            Operational = true,
            ArtifactSourceAvailable = true,
            LibraryStorageWritable = true,
            CheckpointStoreAvailable = true,
            ReconcilerHealthy = true,
        };

        object? result = BuildUnavailableReasonsMethod.Invoke(
            Service,
            [
                slicingEnabled,
                workerAuthenticationConfigured,
                slicingOperational,
                workerHealth,
                true, // calibrationContextOperational
                promotionCapability,
                modelStorageResolvable,
            ]);

        return (List<CapabilityUnavailableReasonDto>)result!;
    }
}
