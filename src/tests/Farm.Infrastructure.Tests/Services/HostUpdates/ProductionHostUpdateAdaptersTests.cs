using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class ProductionHostUpdateAdaptersTests
{
    private const string Platform = "linux-amd64";
    private static readonly string Digest = "sha256:" + new string('a', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidEvidenceMapsDirectSequenceIdentityAndExactlySixTargets(bool explicitTargets)
    {
        VerifiedReleaseEvidenceCache evidence = new();
        VerifiedReleaseEvidenceDto value = Evidence(sequence: long.MaxValue);
        if (explicitTargets)
        {
            value = value with
            {
                ExecutionTargets = value.Services.Select(service => new VerifiedReleaseExecutionTargetDto
                {
                    ServiceId = service.ServiceId,
                    Platform = service.Platform,
                    PlatformDigest = service.PlatformDigest,
                }).ToArray(),
            };
        }
        evidence.SetVerified(value, DateTimeOffset.UtcNow);
        VerifiedReleaseEvidenceCandidateCache cache = new(evidence, new Ready(), Platform, TimeSpan.FromMinutes(10));

        VerifiedHostUpdateCandidate candidate = Assert.IsType<VerifiedHostUpdateCandidate>(cache.Current);
        Assert.Equal(long.MaxValue, candidate.Sequence);
        Assert.Equal("stable:1.2.3", candidate.ReleaseId);
        Assert.Equal("0123456789012345678901234567890123456789", candidate.SourceCommit);
        Assert.Equal(Digest, candidate.PlatformDigests.Api);
        Assert.Null(cache.LastError);
    }

    [Fact]
    public void Current_IncompleteExplicitTargetsWithCompleteInventory_RejectsInsteadOfFallingBack()
    {
        VerifiedReleaseEvidenceDto value = Evidence();
        value = value with
        {
            ExecutionTargets = value.Services.Take(5).Select(service => new VerifiedReleaseExecutionTargetDto
            {
                ServiceId = service.ServiceId,
                Platform = service.Platform,
                PlatformDigest = service.PlatformDigest,
            }).ToArray(),
        };
        VerifiedReleaseEvidenceCache evidence = new();
        evidence.SetVerified(value, DateTimeOffset.UtcNow);
        VerifiedReleaseEvidenceCandidateCache cache = new(
            evidence, new Moq.Mock<IHostUpdateCandidateReadiness>(Moq.MockBehavior.Strict).Object,
            Platform, TimeSpan.FromMinutes(10));

        Assert.Null(cache.Current);
        Assert.Equal("verified_release_target_set_invalid", cache.LastError);
    }

    [Fact]
    public void LastErrorRejectsLastKnownGoodEvidence()
    {
        VerifiedReleaseEvidenceCache evidence = new();
        evidence.SetVerified(Evidence(), DateTimeOffset.UtcNow);
        evidence.SetError("network_failure");
        VerifiedReleaseEvidenceCandidateCache cache = new(evidence, new Ready(), Platform, TimeSpan.FromMinutes(10));

        Assert.Null(cache.Current);
        Assert.Equal("network_failure", cache.LastError);
    }

    [Fact]
    public void StaleEvidenceIsExplicitlyAbsent()
    {
        VerifiedReleaseEvidenceCache evidence = new();
        evidence.SetVerified(Evidence(), DateTimeOffset.UtcNow.AddHours(-1));
        VerifiedReleaseEvidenceCandidateCache cache = new(evidence, new Ready(), Platform, TimeSpan.FromMinutes(10));

        Assert.Null(cache.Current);
        Assert.Equal("verified_release_evidence_stale", cache.LastError);
    }

    [Theory]
    [InlineData(false, true, "verified_release_evidence_untrusted")]
    [InlineData(true, false, "verified_release_evidence_untrusted")]
    public void SignatureAndCompletenessAreRequired(bool signature, bool complete, string reason)
    {
        VerifiedReleaseEvidenceCache evidence = new();
        evidence.SetVerified(Evidence() with { SignatureVerified = signature, IsComplete = complete }, DateTimeOffset.UtcNow);
        VerifiedReleaseEvidenceCandidateCache cache = new(evidence, new Ready(), Platform, TimeSpan.FromMinutes(10));

        Assert.Null(cache.Current);
        Assert.Equal(reason, cache.LastError);
    }

    [Fact]
    public void WrongIdentityDigestAndTargetSetAreRejected()
    {
        foreach (VerifiedReleaseEvidenceDto value in new[]
        {
            Evidence() with { Identity = Evidence().Identity! with { Channel = "unknown" } },
            Evidence() with { ManifestDigest = "not-a-digest" },
            Evidence() with { Services = Evidence().Services.Take(5).ToArray() },
            Evidence() with { Services = Evidence().Services.Select(service => service with { Platform = "linux-arm64" }).ToArray() },
            Evidence() with { Services = Evidence().Services.Select(service => service.ServiceId == "orcaslicer-worker" ? service with { Platform = "linux-arm64" } : service).ToArray() },
        })
        {
            VerifiedReleaseEvidenceCache evidence = new();
            evidence.SetVerified(value, DateTimeOffset.UtcNow);
            VerifiedReleaseEvidenceCandidateCache cache = new(evidence, new Ready(), Platform, TimeSpan.FromMinutes(10));
            Assert.Null(cache.Current);
            Assert.NotNull(cache.LastError);
        }
    }

    [Theory]
    [InlineData("manifest", "verified_release_identity_invalid")]
    [InlineData("manifest-mixed", "verified_release_identity_invalid")]
    [InlineData("platform", "verified_release_target_platform_invalid")]
    [InlineData("platform-mixed", "verified_release_target_platform_invalid")]
    [InlineData("execution-platform", "verified_release_target_platform_invalid")]
    [InlineData("execution-platform-mixed", "verified_release_target_platform_invalid")]
    [InlineData("release-id", "verified_release_identity_invalid")]
    public void Current_NoncanonicalEvidence_RejectsBeforeReadingReadiness(string field, string reason)
    {
        VerifiedReleaseEvidenceDto value = Evidence();
        string digest = "sha256:" + (field.EndsWith("-mixed", StringComparison.Ordinal) ? new string('a', 63) + "B" : new string('A', 64));
        value = field switch
        {
            "manifest" or "manifest-mixed" => value with { ManifestDigest = digest },
            "platform" or "platform-mixed" => value with
            {
                Services = value.Services.Select(service => service with { PlatformDigest = digest }).ToArray(),
            },
            "execution-platform" or "execution-platform-mixed" => value with
            {
                ExecutionTargets = value.Services.Select(service => new VerifiedReleaseExecutionTargetDto
                {
                    ServiceId = service.ServiceId,
                    Platform = service.Platform,
                    PlatformDigest = digest,
                }).ToArray(),
            },
            _ => value with { Identity = value.Identity! with { ReleaseId = "rel-1" } },
        };
        VerifiedReleaseEvidenceCache evidence = new();
        evidence.SetVerified(value, DateTimeOffset.UtcNow);
        VerifiedReleaseEvidenceCandidateCache cache = new(
            evidence, new Moq.Mock<IHostUpdateCandidateReadiness>(Moq.MockBehavior.Strict).Object, Platform, TimeSpan.FromMinutes(10));

        Assert.Null(cache.Current);
        Assert.Equal(reason, cache.LastError);
    }

    [Fact]
    public void SchedulerStatusHolder_ReportsPolicyStateWithoutClaimingExecutionReadiness()
    {
        HostUpdateSchedulerStatusHolder holder = new();
        holder.Update(new HostUpdateSchedulerStatus(
            Enabled: true,
            EffectiveEnabled: true,
            KillSwitch: true,
            Channel: "insider",
            PolicyRevision: 4,
            LastAttemptAt: DateTimeOffset.UtcNow,
            NextPollAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ConsecutiveFailures: 2,
            Reason: HostUpdateSchedulerReason.AdmissionFenceActive));

        UnavailableHostUpdateSchedulingStatusProvider provider = new(
            settings: null!,
            schedulerStatus: holder,
            policyRepository: null);

        HostUpdateSchedulingStatusDto status = provider.GetStatus();

        Assert.True(status.ConfiguredEnabled);
        Assert.True(status.EffectiveEnabled);
        Assert.Equal("insider", status.SelectedChannel);
        Assert.Equal(HostUpdateExecutorState.Unavailable, status.Executor.State);
        Assert.Equal(HostUpdateBackoffState.Waiting, status.Backoff.State);
        Assert.Contains(nameof(HostUpdateSchedulerReason.AdmissionFenceActive), status.Reasons);
        Assert.True(status.KillSwitch.Enabled);
    }

    [Fact]
    public void SchedulerStatusHolder_DoesNotReportExecutorMissingWhenProvisionedButIdle()
    {
        HostUpdateSchedulerStatusHolder holder = new();
        holder.Update(new HostUpdateSchedulerStatus(
            Enabled: false,
            EffectiveEnabled: false,
            KillSwitch: false,
            Channel: "stable",
            PolicyRevision: 0,
            LastAttemptAt: null,
            NextPollAt: null,
            ConsecutiveFailures: 0,
            Reason: HostUpdateSchedulerReason.Disabled));

        UnavailableHostUpdateSchedulingStatusProvider provider = new(
            settings: null!,
            schedulerStatus: holder,
            executor: new AvailableExecutor());

        HostUpdateSchedulingStatusDto status = provider.GetStatus();

        Assert.Equal(HostUpdateExecutorState.Available, status.Executor.State);
        Assert.Null(status.Executor.Reason);
        Assert.DoesNotContain(HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason, status.Reasons);
    }

    [Fact]
    public void SchedulerStatusHolder_AvailableExecutorHasNoExecutorReason()
    {
        HostUpdateSchedulerStatusHolder holder = new();
        holder.Update(new HostUpdateSchedulerStatus(
            Enabled: true,
            EffectiveEnabled: true,
            KillSwitch: false,
            Channel: "stable",
            PolicyRevision: 1,
            LastAttemptAt: DateTimeOffset.UtcNow,
            NextPollAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ConsecutiveFailures: 0,
            Reason: HostUpdateSchedulerReason.Admitted));

        UnavailableHostUpdateSchedulingStatusProvider provider = new(
            settings: null!,
            schedulerStatus: holder,
            executor: new AvailableExecutor());

        HostUpdateSchedulingStatusDto status = provider.GetStatus();

        Assert.Single(status.Reasons, nameof(HostUpdateSchedulerReason.Admitted));
        Assert.DoesNotContain(HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason, status.Reasons);
        Assert.Equal(HostUpdateExecutorState.Available, status.Executor.State);
        Assert.Null(status.Executor.Reason);
    }

    [Fact]
    public void SchedulerStatusHolder_ReportsUnknownExecutorShapeWithoutClaimingItIsUnprovisioned()
    {
        HostUpdateSchedulerStatusHolder holder = new();
        holder.Update(new HostUpdateSchedulerStatus(
            Enabled: true,
            EffectiveEnabled: true,
            KillSwitch: false,
            Channel: "stable",
            PolicyRevision: 1,
            LastAttemptAt: null,
            NextPollAt: null,
            ConsecutiveFailures: 0,
            Reason: HostUpdateSchedulerReason.Disabled));

        UnavailableHostUpdateSchedulingStatusProvider provider = new(
            settings: null!,
            schedulerStatus: holder,
            executor: new ExecutorWithoutAvailability());

        HostUpdateSchedulingStatusDto status = provider.GetStatus();

        Assert.Equal(HostUpdateSchedulingAvailability.ExecutorAvailabilityUnknownReason, status.Executor.Reason);
        Assert.Equal(HostUpdateExecutorState.Unavailable, status.Executor.State);
        Assert.Contains(HostUpdateSchedulingAvailability.ExecutorAvailabilityUnknownReason, status.Reasons);
        Assert.DoesNotContain(HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason, status.Reasons);
    }

    [Fact]
    public void SchedulerStatusHolder_ReplacesMissingUnavailableExecutorReason()
    {
        HostUpdateSchedulerStatusHolder holder = new();
        holder.Update(new HostUpdateSchedulerStatus(
            Enabled: true,
            EffectiveEnabled: true,
            KillSwitch: false,
            Channel: "stable",
            PolicyRevision: 1,
            LastAttemptAt: null,
            NextPollAt: null,
            ConsecutiveFailures: 0,
            Reason: HostUpdateSchedulerReason.Disabled));

        UnavailableHostUpdateSchedulingStatusProvider provider = new(
            settings: null!,
            schedulerStatus: holder,
            executor: new UnavailableExecutor());

        HostUpdateSchedulingStatusDto status = provider.GetStatus();

        Assert.Equal(HostUpdateSchedulingAvailability.ExecutorReasonMissingReason, status.Executor.Reason);
        Assert.Contains(HostUpdateSchedulingAvailability.ExecutorReasonMissingReason, status.Reasons);
        Assert.DoesNotContain(status.Reasons, string.IsNullOrWhiteSpace);
    }

    private static VerifiedReleaseEvidenceDto Evidence(long sequence = 42) => new()
    {
        Sequence = sequence,
        SignatureVerified = true,
        IsComplete = true,
        MinimumUpdaterVersion = "1.0.0",
        ManifestDigest = Digest,
        Identity = new CanonicalReleaseIdentityDto
        {
            ReleaseId = "stable:1.2.3",
            Channel = "stable",
            SourceCommit = "0123456789012345678901234567890123456789",
        },
        Services = new[] { "api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith" }
            .Select((id, index) => new ReleaseServiceRequirementDto { ServiceId = id, Platform = Platform, PlatformDigest = "sha256:" + new string((char)(97 + index), 64), IndexDigest = Digest })
            .ToArray(),
    };

    private sealed class Ready : IHostUpdateCandidateReadiness
    {
        public bool CompatibilityReady => true;
        public bool InstallationAvailable => true;
        public bool SafetyPassed => true;
        public bool IsNewer => true;
    }

    private sealed class AvailableExecutor : IHostUpdateExecutor, IHostUpdateAvailability
    {
        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task<HostUpdateExecutionResult> ExecuteAsync(
            HostUpdateExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostUpdateExecutionResult(
                request.ReleaseId,
                HostUpdateExecutionState.Completed,
                null,
                []));
    }

    private sealed class UnavailableExecutor : IHostUpdateExecutor, IHostUpdateAvailability
    {
        public bool IsAvailable => false;
        public string UnavailableReason => " ";

        public Task<HostUpdateExecutionResult> ExecuteAsync(
            HostUpdateExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostUpdateExecutionResult(
                request.ReleaseId,
                HostUpdateExecutionState.RecoveryRequired,
                null,
                []));
    }

    private sealed class ExecutorWithoutAvailability : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(
            HostUpdateExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostUpdateExecutionResult(
                request.ReleaseId,
                HostUpdateExecutionState.RecoveryRequired,
                null,
                []));
    }
}
