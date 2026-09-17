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
}
