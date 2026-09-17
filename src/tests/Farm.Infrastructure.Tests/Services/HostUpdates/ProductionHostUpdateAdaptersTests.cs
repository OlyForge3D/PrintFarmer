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
    public async Task ExecutorAdapterRefusesContractMismatchWithoutCallingWork()
    {
        UnavailableHostUpdateSchedulerExecutor executor = new();
        HostUpdateExecutorResponse response = await executor.ExecuteAsync(null!, CancellationToken.None);

        Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
        Assert.Equal(UnavailableHostUpdateSchedulerExecutor.Reason, response.Reason);
        await executor.SignalSafeCheckpointCancellationAsync("manual-request", CancellationToken.None);
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
}




