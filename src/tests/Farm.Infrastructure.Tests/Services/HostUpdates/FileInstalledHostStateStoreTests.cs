using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Bishop/Hicks review (issue #2663): a present-but-unusable installed-state file must never read as
/// "no installed state" (which preflight interprets as a first install) and must never surface later
/// as an incidental <see cref="NullReferenceException"/>. Every malformed shape has to fail closed
/// deterministically with <see cref="HostUpdateInstalledStateCorruptException"/> so execute/recovery
/// resolve to NeedsOperator.
/// </summary>
public sealed class FileInstalledHostStateStoreTests
{
    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNullForFirstInstall()
    {
        var store = new FileInstalledHostStateStore(Path.Combine(CreateTempDir(), "installed-state.json"));

        InstalledHostState? state = await store.ReadAsync(CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_RoundTripsValidState()
    {
        string path = Path.Combine(CreateTempDir(), "installed-state.json");
        var store = new FileInstalledHostStateStore(path);
        var written = new InstalledHostState(
            "release-1",
            "sha256:manifest",
            new Dictionary<string, string> { ["api"] = "sha256:api" },
            "monolith",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["api"] = "linux/amd64" });

        await store.WriteAsync(written, CancellationToken.None);
        InstalledHostState? read = await store.ReadAsync(CancellationToken.None);

        read.Should().NotBeNull();
        read!.ReleaseId.Should().Be("release-1");
        read.ServiceDigests.Should().ContainKey("api");
        read.ServicePlatforms.Should().ContainKey("api");
    }

    [Theory]
    [InlineData("null", "record_null")]
    [InlineData("{}", "release_id_missing")]
    [InlineData("""{"ReleaseId":"  ","ManifestDigest":"sha256:m","ServiceDigests":{"api":"sha256:a"},"Topology":"monolith"}""", "release_id_missing")]
    [InlineData("""{"ReleaseId":"release-1","ServiceDigests":{"api":"sha256:a"},"Topology":"monolith"}""", "manifest_digest_missing")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:m","ServiceDigests":{"api":"sha256:a"}}""", "topology_missing")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:m","Topology":"monolith"}""", "service_digests_missing")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:m","ServiceDigests":{},"Topology":"monolith"}""", "service_digests_missing")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:m","ServiceDigests":{"api":""},"Topology":"monolith"}""", "service_digest_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:m","ServiceDigests":{"api":"sha256:a"},"Topology":"monolith","ServicePlatforms":{"api":""}}""", "service_platform_invalid")]
    [InlineData("""{"ReleaseId":"release-1","ManifestDigest":"sha256:m","ServiceDigests":{"api":"sha256:a"},"Topology":"monolith","ServicePlatforms":{"web":"linux/amd64"}}""", "service_platform_unmapped")]
    public async Task ReadAsync_CorruptRecord_FailsClosedWithCode(string json, string expectedCode)
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
