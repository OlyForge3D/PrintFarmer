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
    private static CanonicalReleaseIdentity Identity(string channel = "stable", string version = "1.4.0") => new(
        ReleaseId: $"{channel}:{version}",
        Version: version,
        Channel: channel,
        SourceTag: $"v{version}",
        SourceBranch: channel == "insider" ? "development" : "main",
        SourceCommit: new string('c', 40),
        AuthorizedBranchHead: new string('c', 40),
        BuildMetadata: "build-42",
        OciReleaseLabel: $"v{version}",
        OciVersionLabel: version,
        ManifestDigest: "sha256:" + new string('a', 64));

    [Fact]
    public void ToEvidenceDto_MapsSignatureAndIdentityFields()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 7,
            SignatureVerified: true,
            Identity: Identity(),
            ComponentPlatformDigests: new Dictionary<string, string>
            {
                ["api/linux-amd64"] = "sha256:" + new string('b', 64),
            },
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests("api"),
            ComponentPlatforms: Platforms("api"));

        VerifiedReleaseEvidenceDto dto = metadata.ToEvidenceDto("linux-amd64");

        dto.SignatureVerified.Should().BeTrue();
        dto.IsComplete.Should().BeTrue();
        dto.Sequence.Should().Be(7);
        dto.ManifestDigest.Should().Be(metadata.Identity.ManifestDigest);
        dto.Identity.Should().NotBeNull();
        dto.Identity!.Channel.Should().Be("stable");
        dto.Identity.CanonicalVersion.Should().Be("1.4.0");
        dto.Identity.ReleaseId.Should().Be("stable:1.4.0");
        dto.Identity.SourceCommit.Should().Be(metadata.Identity.SourceCommit);
        dto.MinimumUpdaterVersion.Should().Be("1.0.0");
    }

    [Fact]
    public void ToEvidenceDto_ZeroMajorInsider_MapsCanonicalAndBaseVersionSeparately()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "insider",
            Sequence: 20_000_300_001,
            SignatureVerified: true,
            Identity: Identity("insider", "0.2.3-insider.1"),
            ComponentPlatformDigests: new Dictionary<string, string>
            {
                ["api/linux-amd64"] = "sha256:" + new string('b', 64),
            },
            MinimumUpdaterVersion: "0.0.0",
            ComponentIndexDigests: IndexDigests("api"),
            ComponentPlatforms: Platforms("api"));

        VerifiedReleaseEvidenceDto dto = metadata.ToEvidenceDto("linux-amd64");

        dto.Identity.Should().NotBeNull();
        dto.Identity!.CanonicalVersion.Should().Be("0.2.3-insider.1");
        dto.Identity.BaseVersion.Should().Be("0.2.3");
        dto.Identity.Channel.Should().Be("insider");
        dto.Sequence.Should().Be(20_000_300_001);
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
                ["orcaslicer-worker/linux-amd64"] = "sha256:" + new string('d', 64),
                ["orcaslicer-worker/linux-arm64"] = "sha256:" + new string('e', 64),
            },
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests("api", "orcaslicer-worker"),
            ComponentPlatforms: Platforms("api", "orcaslicer-worker"));

        VerifiedReleaseEvidenceDto dto = metadata.ToEvidenceDto("linux-arm64");

        dto.Services.Should().HaveCount(1);
        dto.Services.Select(service => service.ServiceId).Should().OnlyHaveUniqueItems();
        dto.Services.Should().ContainSingle(service =>
            service.ServiceId == "api" && service.Platform == "linux-arm64" && service.PlatformDigest == "sha256:" + new string('c', 64));
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
                ["api/linux-amd64"] = "sha256:" + new string('e', 64),
            },
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests("api"),
            ComponentPlatforms: new Dictionary<string, IReadOnlyList<string>>
            {
                ["api"] = ["Linux-amd64"],
            });

        Action act = () => metadata.ToEvidenceDto("linux-amd64");

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("linux/amd64")]
    [InlineData("Linux-amd64")]
    [InlineData("-linux-amd64")]
    public void ToEvidenceDto_InvalidOrEmptyHostPlatform_FailsClosed(string hostPlatform)
    {
        SignedReleaseMetadata metadata = new(
            "stable",
            1,
            true,
            Identity(),
            new Dictionary<string, string>
            {
                ["api/linux-amd64"] = "sha256:" + new string('b', 64),
            },
            "1.0.0",
            IndexDigests("api"),
            Platforms("api"));

        Action act = () => metadata.ToEvidenceDto(hostPlatform);

        if (string.IsNullOrWhiteSpace(hostPlatform))
        {
            act.Should().Throw<ArgumentException>();
        }
        else
        {
            act.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void ToEvidenceDto_ManifestVocabulary_MapsObservedServicesAndOmitsPackagingOnlyMonolith()
    {
        string platformDigest = "sha256:" + new string('b', 64);
        string indexDigest = "sha256:" + new string('a', 64);
        string[] manifestServices =
        [
            "api",
            "frontend",
            "slicer-host",
            "printer-discovery",
            "orcaslicer-worker",
            "monolith",
        ];
        SignedReleaseMetadata metadata = new(
            "stable",
            99_999,
            true,
            Identity(),
            manifestServices.ToDictionary(
                id => $"{id}/linux-amd64",
                _ => platformDigest,
                StringComparer.Ordinal),
            "1.0.0",
            IndexDigests(manifestServices),
            Platforms(manifestServices));

        VerifiedReleaseEvidenceDto dto = metadata.ToEvidenceDto("linux-amd64");

        dto.Services.Select(service => service.ServiceId).Should().BeEquivalentTo(
            ["api", "frontend", "slicer-host", "discovery", "slicer-worker"]);
        dto.Services.Should().OnlyContain(service =>
            service.PlatformDigest == platformDigest && service.IndexDigest == indexDigest);
        dto.ExecutionTargets.Select(target => target.ServiceId).Should().BeEquivalentTo(manifestServices);
        dto.ExecutionTargets.Should().OnlyContain(target =>
            target.Platform == "linux-amd64" && target.PlatformDigest == platformDigest);
    }

    public static IEnumerable<object[]> NoncanonicalDigests()
    {
        string canonical = "sha256:" + new string('a', 64);
        string[] invalidDigests =
        [
            "sha256:" + new string('A', 64),
            "sha256:" + new string('a', 63) + "B",
            "SHA256:" + new string('a', 64),
            "sha256:" + new string('g', 64),
            canonical[..^1],
            canonical + "a",
            " " + canonical,
            canonical + "\n",
            string.Empty,
        ];
        foreach (string serviceId in new[] { "api", "monolith" })
        {
            foreach (bool indexDigest in new[] { false, true })
            {
                foreach (string digest in invalidDigests)
                {
                    yield return [serviceId, indexDigest, digest];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(NoncanonicalDigests))]
    public void ToEvidenceDto_NoncanonicalChildOrIndexDigest_RejectsWithoutNormalizing(
        string serviceId, bool indexDigest, string digest)
    {
        string canonical = "sha256:" + new string('a', 64);
        Dictionary<string, string> childDigests = new(StringComparer.Ordinal)
        {
            ["api/linux-amd64"] = canonical,
            ["monolith/linux-amd64"] = canonical,
        };
        Dictionary<string, string> indexDigests = IndexDigests("api", "monolith");
        if (indexDigest)
        {
            indexDigests[serviceId] = digest;
        }
        else
        {
            childDigests[$"{serviceId}/linux-amd64"] = digest;
        }
        SignedReleaseMetadata metadata = new(
            "stable", 1, true, Identity(), childDigests, "1.0.0",
            indexDigests, Platforms("api", "monolith"));

        Action act = () => metadata.ToEvidenceDto("linux-amd64");

        act.Should().Throw<InvalidDataException>().WithMessage($"*'{serviceId}'*");
        (indexDigest ? indexDigests[serviceId] : childDigests[$"{serviceId}/linux-amd64"])
            .Should().Be(digest);
    }

    [Theory]
    [InlineData("missing-child")]
    [InlineData("missing-index")]
    [InlineData("duplicate-platform")]
    [InlineData("unknown-service")]
    public void ToEvidenceDto_InvalidMonolithEvidence_Rejects(string defect)
    {
        Dictionary<string, string> children = new(StringComparer.Ordinal)
        {
            ["api/linux-amd64"] = "sha256:" + new string('b', 64),
            ["monolith/linux-amd64"] = "sha256:" + new string('c', 64),
        };
        Dictionary<string, string> indexes = IndexDigests("api", "monolith");
        Dictionary<string, IReadOnlyList<string>> platforms = Platforms("api", "monolith");
        switch (defect)
        {
            case "missing-child":
                children.Remove("monolith/linux-amd64");
                break;
            case "missing-index":
                indexes.Remove("monolith");
                break;
            case "duplicate-platform":
                platforms["monolith"] = ["linux-amd64", "linux-amd64"];
                break;
            case "unknown-service":
                platforms["unknown"] = ["linux-amd64"];
                break;
        }
        SignedReleaseMetadata metadata = new(
            "stable", 1, true, Identity(), children, "1.0.0", indexes, platforms);

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

    [Fact]
    public void ToEvidenceDto_NullPlatformDigests_Rejects()
    {
        SignedReleaseMetadata metadata = new(
            "stable",
            1,
            true,
            Identity(),
            null!,
            "1.0.0",
            IndexDigests("api"),
            Platforms("api"));

        Action act = () => metadata.ToEvidenceDto("linux-amd64");

        act.Should().Throw<InvalidDataException>();
    }

    private static Dictionary<string, string> IndexDigests(params string[] services) =>
        services.ToDictionary(
            service => service,
            _ => "sha256:" + new string('a', 64),
            StringComparer.Ordinal);

    private static Dictionary<string, IReadOnlyList<string>> Platforms(params string[] services) =>
        services.ToDictionary(
            service => service,
            service => service == "orcaslicer-worker"
                ? (IReadOnlyList<string>)["linux-amd64"]
                : ["linux-amd64", "linux-arm64"],
            StringComparer.Ordinal);
}
