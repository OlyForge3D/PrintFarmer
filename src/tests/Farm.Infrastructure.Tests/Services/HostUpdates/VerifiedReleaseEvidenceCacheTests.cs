using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for <see cref="VerifiedReleaseEvidenceCache"/> (issue #2757 item 3): the
/// cache must retain its last verified evidence across a subsequent discovery failure -- "no
/// success-shaped fallback", but also no regression to "no evidence" on a transient error.
/// </summary>
public class VerifiedReleaseEvidenceCacheTests
{
    private static VerifiedReleaseEvidenceDto Evidence(string channel = "stable") => new()
    {
        Sequence = 123_456_789_012,
        SignatureVerified = true,
        IsComplete = true,
        ManifestDigest = "sha256:" + new string('a', 64),
        Identity = new CanonicalReleaseIdentityDto
        {
            CanonicalVersion = "1.2.3",
            BaseVersion = "1.2.3",
            Channel = channel,
            ReleaseId = $"{channel}:1.2.3",
        },
        Services = [],
    };

    [Fact]
    public void Current_BeforeAnyDiscovery_IsNull()
    {
        var cache = new VerifiedReleaseEvidenceCache();

        cache.Current.Should().BeNull();
        cache.LastVerifiedAt.Should().BeNull();
        cache.LastError.Should().BeNull();
    }

    [Fact]
    public void SetVerified_StoresEvidenceAndClearsError()
    {
        var cache = new VerifiedReleaseEvidenceCache();
        cache.SetError("prior transient failure");

        DateTimeOffset verifiedAt = DateTimeOffset.UtcNow;
        VerifiedReleaseEvidenceDto evidence = Evidence();
        cache.SetVerified(evidence, verifiedAt);

        cache.Current.Should().BeSameAs(evidence);
        cache.Current!.Sequence.Should().Be(123_456_789_012);
        cache.LastVerifiedAt.Should().Be(verifiedAt);
        cache.LastError.Should().BeNull();
    }

    [Fact]
    public void SetError_AfterSuccessfulVerification_RetainsPreviouslyCachedEvidence()
    {
        var cache = new VerifiedReleaseEvidenceCache();
        VerifiedReleaseEvidenceDto evidence = Evidence();
        DateTimeOffset verifiedAt = DateTimeOffset.UtcNow;
        cache.SetVerified(evidence, verifiedAt);

        cache.SetError("GitHub API returned 503");

        cache.Current.Should().BeSameAs(evidence, "a transient discovery failure must not regress readiness to \"no evidence\"");
        cache.LastVerifiedAt.Should().Be(verifiedAt);
        cache.LastError.Should().Be("GitHub API returned 503");
    }

    [Fact]
    public void SetError_BeforeAnyVerification_LeavesCurrentNull()
    {
        var cache = new VerifiedReleaseEvidenceCache();

        cache.SetError("no verified release ever discovered");

        cache.Current.Should().BeNull();
        cache.LastVerifiedAt.Should().BeNull();
        cache.LastError.Should().Be("no verified release ever discovered");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetError_EmptyMessage_NormalizesWithoutThrowing(string error)
    {
        var cache = new VerifiedReleaseEvidenceCache();

        cache.SetError(error);

        cache.LastError.Should().Be("Verified release discovery failed without an error message.");
    }

    [Fact]
    public void SetVerified_OlderSequence_DoesNotReplaceNewerEvidence()
    {
        var cache = new VerifiedReleaseEvidenceCache();
        VerifiedReleaseEvidenceDto newer = Evidence() with { Sequence = 20 };
        VerifiedReleaseEvidenceDto older = Evidence() with { Sequence = 19 };

        cache.SetVerified(newer, DateTimeOffset.UtcNow);
        cache.SetVerified(older, DateTimeOffset.UtcNow.AddSeconds(1));

        cache.Current.Should().BeSameAs(newer);
        cache.LastVerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public void SetVerified_LowerSequenceOnDifferentChannel_ReplacesEvidence()
    {
        // Insider sequences are always numerically higher than stable sequences for the same
        // major.minor.patch (see SignedUpdateManifestValidator.DeriveSequence's suffix ranges),
        // so a global sequence comparison would permanently lock the cache onto insider evidence
        // once an operator has ever discovered an insider release. Monotonicity must be scoped
        // to the evidence's own channel: an operator switching the update channel setting back
        // to stable must see that stable evidence replace the cached insider evidence, even
        // though its Sequence is numerically lower.
        var cache = new VerifiedReleaseEvidenceCache();
        VerifiedReleaseEvidenceDto insider = Evidence(channel: "insider") with { Sequence = 100_000_000_000 };
        VerifiedReleaseEvidenceDto stable = Evidence(channel: "stable") with { Sequence = 1_000_000 };

        DateTimeOffset insiderVerifiedAt = DateTimeOffset.UtcNow;
        cache.SetVerified(insider, insiderVerifiedAt);
        cache.Current.Should().BeSameAs(insider);

        DateTimeOffset stableVerifiedAt = insiderVerifiedAt.AddMinutes(5);
        cache.SetVerified(stable, stableVerifiedAt);

        cache.Current.Should().BeSameAs(stable, "an operator channel switch must not be blocked by the other channel's higher sequence");
        cache.LastVerifiedAt.Should().Be(stableVerifiedAt, "the replacement must record its own verification timestamp, not retain the prior channel's");
    }

    [Fact]
    public void SetVerified_LowerSequenceOnSameChannel_StillRejected()
    {
        // Companion to the cross-channel test above: monotonicity protection must still apply
        // within a single channel -- only the cross-channel comparison is disabled.
        var cache = new VerifiedReleaseEvidenceCache();
        VerifiedReleaseEvidenceDto newerStable = Evidence(channel: "stable") with { Sequence = 2_000_000 };
        VerifiedReleaseEvidenceDto olderStable = Evidence(channel: "stable") with { Sequence = 1_000_000 };

        DateTimeOffset newerVerifiedAt = DateTimeOffset.UtcNow;
        cache.SetVerified(newerStable, newerVerifiedAt);
        bool accepted = cache.SetVerified(olderStable, newerVerifiedAt.AddMinutes(5));

        accepted.Should().BeFalse();
        cache.Current.Should().BeSameAs(newerStable);
        cache.LastVerifiedAt.Should().Be(newerVerifiedAt);
    }

    [Fact]
    public void GetSnapshot_ReturnsAtomicViewOfEvidenceState()
    {
        var cache = new VerifiedReleaseEvidenceCache();
        VerifiedReleaseEvidenceDto evidence = Evidence();
        DateTimeOffset verifiedAt = DateTimeOffset.UtcNow;
        cache.SetVerified(evidence, verifiedAt);

        VerifiedReleaseEvidenceCacheSnapshot snapshot = cache.GetSnapshot();

        snapshot.Current.Should().BeSameAs(evidence);
        snapshot.LastVerifiedAt.Should().Be(verifiedAt);
        snapshot.LastError.Should().BeNull();
    }
}
