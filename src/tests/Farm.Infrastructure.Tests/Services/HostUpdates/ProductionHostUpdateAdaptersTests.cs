using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class ProductionHostUpdateAdaptersTests
{
    private const string Platform = "linux-amd64";
    private static readonly string Digest = "sha256:" + new string('a', 64);

    [Fact]
    public void ValidEvidenceMapsDirectSequenceIdentityAndExactlySixTargets()
    {
        VerifiedReleaseEvidenceCache evidence = new();
        evidence.SetVerified(Evidence(sequence: long.MaxValue), DateTimeOffset.UtcNow);
        VerifiedReleaseEvidenceCandidateCache cache = new(evidence, new Ready(), Platform, TimeSpan.FromMinutes(10));

        VerifiedHostUpdateCandidate candidate = Assert.IsType<VerifiedHostUpdateCandidate>(cache.Current);
        Assert.Equal(long.MaxValue, candidate.Sequence);
        Assert.Equal("stable:1.2.3", candidate.ReleaseId);
        Assert.Equal("0123456789012345678901234567890123456789", candidate.SourceCommit);
        Assert.Equal(Digest, candidate.PlatformDigests.Api);
        Assert.Null(cache.LastError);
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
    public void SchedulerStatusHolder_ReportsAdmittedReasonOnceForAvailableExecutor()
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

        Assert.Equal(1, status.Reasons.Count(reason => reason == nameof(HostUpdateSchedulerReason.Admitted)));
        Assert.DoesNotContain(HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason, status.Reasons);
        Assert.Equal(HostUpdateExecutorState.Available, status.Executor.State);
        Assert.Null(status.Executor.Reason);
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
}
