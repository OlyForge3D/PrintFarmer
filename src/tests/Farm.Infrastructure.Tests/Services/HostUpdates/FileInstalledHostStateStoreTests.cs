using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Bishop/Hicks review (issue #2663): the installed-state file is the sole evidence recovery uses to
/// decide what the host may be rolled back to, so it must be validated as semantically canonical
/// rather than merely non-blank. A present-but-unusable file must never read as "no installed state"
/// (which preflight interprets as a first install), never surface later as an incidental
/// NullReferenceException, and never drive a restore toward a state that was never actually
/// installed. Every malformed shape fails closed with
/// <see cref="HostUpdateInstalledStateCorruptException"/> so execute/recovery resolve to NeedsOperator.
/// </summary>
public sealed class FileInstalledHostStateStoreTests
{
    private const string ManifestDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ApiDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string WebDigest = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNullForFirstInstall()
    {
        var store = new FileInstalledHostStateStore(Path.Combine(CreateTempDir(), "installed-state.json"));

        InstalledHostState? state = await store.ReadAsync(CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_RoundTripsCanonicalState()
    {
        string path = Path.Combine(CreateTempDir(), "installed-state.json");
        var store = new FileInstalledHostStateStore(path);
        var written = new InstalledHostState(
            "release-1",
            ManifestDigest,
            new Dictionary<string, string> { ["web"] = WebDigest, ["api"] = ApiDigest },
            "api+web",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["api"] = "linux-amd64", ["web"] = "linux-arm64" });

        await store.WriteAsync(written, CancellationToken.None);
        InstalledHostState? read = await store.ReadAsync(CancellationToken.None);

        read.Should().NotBeNull();
        read!.ReleaseId.Should().Be("release-1");
        read.ManifestDigest.Should().Be(ManifestDigest);
        read.ServiceDigests.Should().HaveCount(2).And.ContainKey("api");
        read.ServicePlatforms.Should().NotBeNull();
        read.ServicePlatforms!.Should().HaveCount(2);
        read.Topology.Should().Be("api+web");
    }

    [Fact]
    public async Task WriteAsync_NonCanonicalState_FailsClosedBeforePersisting()
    {
        string path = Path.Combine(CreateTempDir(), "installed-state.json");
        var store = new FileInstalledHostStateStore(path);
        var invalid = new InstalledHostState(
            "release-1",
            ManifestDigest,
            new Dictionary<string, string> { ["api"] = ApiDigest },
            "monolith",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["api"] = "linux-amd64" });

        Func<Task> write = () => store.WriteAsync(invalid, CancellationToken.None);

        (await write.Should().ThrowAsync<HostUpdateInstalledStateCorruptException>())
            .Which.Code.Should().Be("topology_invalid");
        File.Exists(path).Should().BeFalse();
    }

    [Theory]

    // Absent or structurally empty records.
    [InlineData("null", "record_null")]
    [InlineData("{}", "release_id_invalid")]

    // Release identity must be a canonical identifier, not merely non-blank.
    [InlineData("""{"ReleaseId":"  ","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "release_id_invalid")]
    [InlineData("""{"ReleaseId":"../escape","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "release_id_invalid")]

    // Manifest digest must be a canonical SHA-256 digest, not any non-blank string.
    [InlineData("""{"ReleaseId":"release-1","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "manifest_digest_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:abc","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "manifest_digest_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"md5:11111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "manifest_digest_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:zzzz111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "manifest_digest_invalid")]

    // The service digest map must be present and non-empty.
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "service_digests_missing")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{},"Topology":"","ServicePlatforms":{}}""", "service_digests_missing")]

    // Service ids must be canonical identifiers.
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"","ServicePlatforms":{"":"linux-amd64"}}""", "service_id_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api/../web":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api/../web","ServicePlatforms":{"api/../web":"linux-amd64"}}""", "service_id_invalid")]

    // Service digests must be canonical SHA-256 digests.
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":""},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "service_digest_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:api"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64"}}""", "service_digest_invalid")]

    // The platform map must be present and describe exactly the same service set as the digests.
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api"}""", "service_platforms_missing")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{}}""", "service_platforms_mismatch")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222","web":"sha256:3333333333333333333333333333333333333333333333333333333333333333"},"Topology":"api+web","ServicePlatforms":{"api":"linux-amd64"}}""", "service_platforms_mismatch")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64","web":"linux-amd64"}}""", "service_platforms_mismatch")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222","web":"sha256:3333333333333333333333333333333333333333333333333333333333333333"},"Topology":"api+web","ServicePlatforms":{"api":"linux-amd64","proxy":"linux-amd64"}}""", "service_platforms_mismatch")]

    // Platform values must be canonical platform identifiers.
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":""}}""", "service_platform_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"linux/amd64"}}""", "service_platform_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"api","ServicePlatforms":{"api":"plan9-sparc"}}""", "service_platform_invalid")]

    // Topology must equal the canonical projection of the recorded service ids.
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"","ServicePlatforms":{"api":"linux-amd64"}}""", "topology_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},"Topology":"monolith","ServicePlatforms":{"api":"linux-amd64"}}""", "topology_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222","web":"sha256:3333333333333333333333333333333333333333333333333333333333333333"},"Topology":"web+api","ServicePlatforms":{"api":"linux-amd64","web":"linux-amd64"}}""", "topology_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222","web":"sha256:3333333333333333333333333333333333333333333333333333333333333333"},"Topology":"api+api+web","ServicePlatforms":{"api":"linux-amd64","web":"linux-amd64"}}""", "topology_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222","web":"sha256:3333333333333333333333333333333333333333333333333333333333333333"},"Topology":"api+web+proxy","ServicePlatforms":{"api":"linux-amd64","web":"linux-amd64"}}""", "topology_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","ServiceDigests":{"api":"sha256:2222222222222222222222222222222222222222222222222222222222222222","web":"sha256:3333333333333333333333333333333333333333333333333333333333333333"},"Topology":"api","ServicePlatforms":{"api":"linux-amd64","web":"linux-amd64"}}""", "topology_invalid")]
    public async Task ReadAsync_NonCanonicalRecord_FailsClosedWithCode(string json, string expectedCode)
    {
        string path = Path.Combine(CreateTempDir(), "installed-state.json");
        await File.WriteAllTextAsync(path, json, CancellationToken.None);
        var store = new FileInstalledHostStateStore(path);

        Func<Task> read = () => store.ReadAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<HostUpdateInstalledStateCorruptException>())
            .Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task ReadAsync_TornJson_FailsClosedAsInvalidJson()
    {
        string path = Path.Combine(CreateTempDir(), "installed-state.json");
        await File.WriteAllTextAsync(path, """{"ReleaseId":"release-1","Manif""", CancellationToken.None);
        var store = new FileInstalledHostStateStore(path);

        Func<Task> read = () => store.ReadAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<HostUpdateInstalledStateCorruptException>())
            .Which.Code.Should().Be("json_invalid");
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "pf-installed-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
