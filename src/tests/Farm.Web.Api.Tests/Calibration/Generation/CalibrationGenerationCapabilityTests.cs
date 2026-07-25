using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Covers the per-hop calibration generation capability in both deployment topologies.
/// </summary>
/// <remarks>
/// Every case removes exactly one real production hop and asserts that the capability turns false with
/// the reason that names it. Nothing here infers readiness from a configuration switch.
/// </remarks>
public sealed class CalibrationGenerationCapabilityTests : IAsyncLifetime
{
    private CalibrationGenerationHarness _harness = null!;

    public async Task InitializeAsync() => _harness = await CalibrationGenerationHarness.CreateAsync();

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "A complete monolith with an attested worker reports generation operational")]
    public async Task GetCapabilityAsync_WithCompleteMonolith_IsOperational()
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeTrue(capability.UnavailableCode);
        _ = capability.DeterministicCoreAvailable.Should().BeTrue();
        _ = capability.ModelStorageRoutable.Should().BeTrue();
        _ = capability.SliceSubmissionRoutable.Should().BeTrue();
        _ = capability.ArtifactSourceRoutable.Should().BeTrue();
        _ = capability.PinnedWorkerAvailable.Should().BeTrue();
        _ = capability.PromotionOperational.Should().BeTrue();
        _ = capability.OrchestrationStoreAvailable.Should().BeTrue();
        _ = capability.RecoveryHealthy.Should().BeTrue();
        _ = capability.UnavailableCode.Should().BeNull();
    }

    [Fact(DisplayName = "Generation stays false without the deterministic generation core")]
    public async Task GetCapabilityAsync_WithoutDeterministicCore_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions
            {
                DeterministicCoreAvailable = false,
            })
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.DeterministicCoreAvailable.Should().BeFalse();
        _ = capability.UnavailableCode.Should().Be("generation_core_unavailable");
    }

    [Fact(DisplayName = "Generation stays false without authorized model storage")]
    public async Task GetCapabilityAsync_WithoutModelStorage_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions
            {
                ModelStorageRoutable = false,
            })
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.ModelStorageRoutable.Should().BeFalse();
        _ = capability.UnavailableCode.Should()
            .Be(CalibrationGenerationProblemCodes.ModelStorageUnavailable);
    }

    [Fact(DisplayName = "Generation stays false without the canonical slice submission path")]
    public async Task GetCapabilityAsync_WithoutSliceSubmission_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions
            {
                SliceSubmissionRoutable = false,
            })
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.SliceSubmissionRoutable.Should().BeFalse();
        _ = capability.UnavailableCode.Should()
            .Be(CalibrationGenerationProblemCodes.SliceSubmissionUnavailable);
    }

    [Fact(DisplayName = "Generation stays false when artifacts are not routable")]
    public async Task GetCapabilityAsync_WithoutArtifactSource_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions
            {
                ArtifactSourceRoutable = false,
            })
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.ArtifactSourceRoutable.Should().BeFalse();
        _ = capability.UnavailableCode.Should().Be("artifact_source_unroutable");
    }

    [Fact(DisplayName = "Generation stays false when the promotion hop is not operational")]
    public async Task GetCapabilityAsync_WithoutPromotion_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions
            {
                PromotionRoutable = false,
            })
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.PromotionOperational.Should().BeFalse();
        _ = capability.UnavailableCode.Should()
            .Be(CalibrationGenerationProblemCodes.PromotionUnavailable);
    }

    [Fact(DisplayName = "Generation stays false while no worker attests the pinned build identity")]
    public async Task GetCapabilityAsync_WithoutAttestedWorker_IsUnavailable()
    {
        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.PinnedWorkerAvailable.Should().BeFalse();
        _ = capability.UnavailableCode.Should()
            .Be(CalibrationGenerationProblemCodes.PinnedWorkerUnavailable);
    }

    [Fact(DisplayName = "A worker without both reproducible build digests is not an attestation")]
    public async Task GetCapabilityAsync_WithoutBinaryDigest_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync(binaryDigest: null);

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.PinnedWorkerAvailable.Should().BeFalse();
        _ = capability.Operational.Should().BeFalse();
    }

    [Fact(DisplayName = "A worker running an unpinned slicer version is not eligible")]
    public async Task GetCapabilityAsync_WithMismatchedVersion_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync(version: "2.2.0");

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.PinnedWorkerAvailable.Should().BeFalse();
        _ = capability.Operational.Should().BeFalse();
    }

    [Fact(DisplayName = "Generation stays false while slicing is disabled for the deployment")]
    public async Task GetCapabilityAsync_WithSlicingDisabled_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions
            {
                SlicingEnabled = false,
            })
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.PinnedWorkerAvailable.Should().BeFalse();
    }

    [Theory(DisplayName = "A split host stays false and names the missing routing adapters")]
    [InlineData("split")]
    [InlineData("microservices")]
    public async Task GetCapabilityAsync_InSplitDeployment_IsUnavailable(string deploymentMode)
    {
        _ = await _harness.AddAttestedWorkerAsync();

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions
            {
                DeploymentMode = deploymentMode,
                ModelStorageRoutable = false,
                SliceSubmissionRoutable = false,
                ArtifactSourceRoutable = false,
                PromotionRoutable = false,
            })
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.UnavailableCode.Should().Be("split_routing_unavailable");
    }

    [Fact(DisplayName = "Generation stays false while the recovery loop is unhealthy")]
    public async Task GetCapabilityAsync_WithUnhealthyRecovery_IsUnavailable()
    {
        _ = await _harness.AddAttestedWorkerAsync();
        for (int failure = 0; failure < 3; failure++)
        {
            _harness.RecoveryState.RecordFailure();
        }

        CalibrationGenerationCapabilityDto capability = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.RecoveryHealthy.Should().BeFalse();
        _ = capability.Operational.Should().BeFalse();
        _ = capability.UnavailableCode.Should().Be("generation_recovery_unavailable");
    }

    [Fact(DisplayName = "The attested identity comes from the worker registry, never from configuration")]
    public async Task FindPinnedWorkerAsync_ReturnsRegisteredAttestation()
    {
        Guid workerId = await _harness.AddAttestedWorkerAsync();

        CalibrationPinnedSlicerIdentity? pinned = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
            .FindPinnedWorkerAsync(CancellationToken.None);

        _ = pinned.Should().NotBeNull();
        _ = pinned!.WorkerId.Should().Be(workerId);
        _ = pinned.ContainerDigest.Should().Be(CalibrationGenerationHarness.ContainerDigest);
        _ = pinned.BinarySha256.Should().Be(CalibrationGenerationHarness.BinaryDigest);
        _ = pinned.Version.Should().Be("2.3.1");
        _ = pinned.Distribution.Should().Be("upstream");
    }
}
