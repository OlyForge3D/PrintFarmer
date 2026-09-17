using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for <see cref="VerifiedReleaseEvidenceMapper"/> (issue #2757
/// item 4): translating the host-update-domain <see cref="SignedReleaseMetadata"/> shape into
/// the inventory-domain <see cref="VerifiedReleaseEvidenceDto"/> shape
/// <c>ReleaseReadinessEvaluator</c> consumes, including splitting
/// <c>ComponentPlatformDigests</c>' <c>"{serviceId}/{platform}"</c> keys.
/// </summary>
public class VerifiedReleaseEvidenceMapperTests
{
    private static CanonicalReleaseIdentity Identity(string channel = "stable") => new(
        ReleaseId: $"{channel}:1.4.0",
        Version: "1.4.0",
        Channel: channel,
        SourceTag: "v1.4.0",
        SourceBranch: "main",
        SourceCommit: new string('c', 40),
        AuthorizedBranchHead: new string('c', 40),
        BuildMetadata: "build-42",
        OciReleaseLabel: "v1.4.0",
        OciVersionLabel: "1.4.0",
        ManifestDigest: "sha256:" + new string('a', 64));

    [Fact]
    public void ToEvidenceDto_MapsSignatureAndIdentityFields()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 7,
            SignatureVerified: true,
            Identity: Identity(),
            ComponentPlatformDigests: new Dictionary<string, string>(),
            MinimumUpdaterVersion: "1.0.0");

        VerifiedReleaseEvidenceDto dto = metadata.ToEvidenceDto("linux-amd64");

        dto.SignatureVerified.Should().BeTrue();
        dto.IsComplete.Should().BeTrue();
        dto.ManifestDigest.Should().Be(metadata.Identity.ManifestDigest);
        dto.Identity.Should().NotBeNull();
        dto.Identity!.Channel.Should().Be("stable");
        dto.Identity.CanonicalVersion.Should().Be("1.4.0");
        dto.Identity.ReleaseId.Should().Be("stable:1.4.0");
        dto.Identity.SourceCommit.Should().Be(metadata.Identity.SourceCommit);
    }

    [Fact]
    public void ToEvidenceDto_TwoPlatforms_SelectsDistinctHostChildDigestOncePerService()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 1,
            SignatureVerified: true,
            Identity: Identity(),
            ComponentPlatformDigests: new Dictionary<string, string>
            {
                ["api/linux-amd64"] = "sha256:" + new string('b', 64),
                ["api/linux-arm64"] = "sha256:" + new string('c', 64),
                ["slicer-worker/linux-amd64"] = "sha256:" + new string('d', 64),
                ["slicer-worker/linux-arm64"] = "sha256:" + new string('e', 64),
            },
            MinimumUpdaterVersion: "1.0.0");

        VerifiedReleaseEvidenceDto dto = metadata.ToEvidenceDto("linux-arm64");

        dto.Services.Should().HaveCount(2);
        dto.Services.Select(service => service.ServiceId).Should().OnlyHaveUniqueItems();
        dto.Services.Should().ContainSingle(service =>
            service.ServiceId == "api" && service.Platform == "linux-arm64" && service.PlatformDigest == "sha256:" + new string('c', 64));
        dto.Services.Should().ContainSingle(service =>
            service.ServiceId == "slicer-worker" && service.Platform == "linux-arm64" && service.PlatformDigest == "sha256:" + new string('e', 64));
    }

    [Fact]
    public void ToEvidenceDto_MalformedComponentKey_Rejects()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 1,
            SignatureVerified: true,
            Identity: Identity(),
            ComponentPlatformDigests: new Dictionary<string, string>
            {
                ["malformed-no-separator"] = "sha256:" + new string('d', 64),
                ["api/linux-amd64"] = "sha256:" + new string('e', 64),
            },
            MinimumUpdaterVersion: "1.0.0");

        Action act = () => metadata.ToEvidenceDto("linux-amd64");

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ToEvidenceDto_NullMetadata_Throws()
    {
        SignedReleaseMetadata? metadata = null;

        Action act = () => metadata!.ToEvidenceDto("linux-amd64");

        act.Should().Throw<ArgumentNullException>();
    }
}
