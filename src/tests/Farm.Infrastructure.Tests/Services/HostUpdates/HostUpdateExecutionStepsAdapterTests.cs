using Farm.Infrastructure.Services.HostUpdates;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutionStepsAdapterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hu-steps-").FullName;

    [Theory]
    [InlineData("linux-amd64")]
    [InlineData("linux-arm64")]
    public async Task VerifyAsync_VerifiedTargets_PersistsCompleteStateBeforeReleasingFence(string platform)
    {
        HostUpdateExecutionRequest request = Request(platform);
        Assert.True(request.IsValid(out string error), error);
        string path = Path.Combine(_root, "installed-state.json");
        var store = new FileInstalledHostStateStore(path);
        var verifier = new Mock<IHostUpdateHealthVerifier>(MockBehavior.Strict);
        verifier.Setup(value => value.RunAsync(request, CancellationToken.None))
            .Returns(() =>
            {
                Assert.False(File.Exists(path));
                return Task.CompletedTask;
            });
        var fence = new Mock<IHostUpdateFenceCoordinator>(MockBehavior.Strict);
        fence.Setup(value => value.ReleaseAsync(CancellationToken.None))
            .Returns(async () => await AssertPersistedStateAsync(path, request));
        HostUpdateExecutionStepsAdapter adapter = CreateAdapter(store, verifier.Object, fence.Object);

        await adapter.VerifyAsync(request, CancellationToken.None);

        await AssertPersistedStateAsync(path, request);
        verifier.Verify(value => value.RunAsync(request, CancellationToken.None), Times.Once);
        fence.Verify(value => value.ReleaseAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_HealthVerificationFails_PreservesPriorStateAndKeepsFenceClosed()
    {
        HostUpdateExecutionRequest request = Request("linux-amd64");
        string path = Path.Combine(_root, "installed-state.json");
        var store = new FileInstalledHostStateStore(path);
        await WritePriorStateAsync(store);
        string prior = await File.ReadAllTextAsync(path);
        var verifier = new Mock<IHostUpdateHealthVerifier>(MockBehavior.Strict);
        verifier.Setup(value => value.RunAsync(request, CancellationToken.None))
            .ThrowsAsync(new HostUpdateVerificationTimeoutException(["digest:api"]));
        var fence = new Mock<IHostUpdateFenceCoordinator>(MockBehavior.Strict);
        HostUpdateExecutionStepsAdapter adapter = CreateAdapter(store, verifier.Object, fence.Object);

        await Assert.ThrowsAsync<HostUpdateVerificationTimeoutException>(() =>
            adapter.VerifyAsync(request, CancellationToken.None));

        Assert.Equal(prior, await File.ReadAllTextAsync(path));
        fence.Verify(value => value.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_InvalidTargetPlatform_PreservesPriorStateAndKeepsFenceClosed()
    {
        HostUpdateExecutionRequest request = Request("invalid-platform");
        string path = Path.Combine(_root, "installed-state.json");
        var store = new FileInstalledHostStateStore(path);
        await WritePriorStateAsync(store);
        string prior = await File.ReadAllTextAsync(path);
        var verifier = new Mock<IHostUpdateHealthVerifier>(MockBehavior.Strict);
        verifier.Setup(value => value.RunAsync(request, CancellationToken.None)).Returns(Task.CompletedTask);
        var fence = new Mock<IHostUpdateFenceCoordinator>(MockBehavior.Strict);
        HostUpdateExecutionStepsAdapter adapter = CreateAdapter(store, verifier.Object, fence.Object);

        HostUpdateInstalledStateCorruptException exception =
            await Assert.ThrowsAsync<HostUpdateInstalledStateCorruptException>(() =>
                adapter.VerifyAsync(request, CancellationToken.None));

        Assert.Equal("service_platform_invalid", exception.Code);
        Assert.Equal(prior, await File.ReadAllTextAsync(path));
        fence.Verify(value => value.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_FenceReleaseFails_RetainsReadableVerifiedState()
    {
        HostUpdateExecutionRequest request = Request("linux-amd64");
        string path = Path.Combine(_root, "installed-state.json");
        var store = new FileInstalledHostStateStore(path);
        var verifier = new Mock<IHostUpdateHealthVerifier>(MockBehavior.Strict);
        verifier.Setup(value => value.RunAsync(request, CancellationToken.None)).Returns(Task.CompletedTask);
        var fence = new Mock<IHostUpdateFenceCoordinator>(MockBehavior.Strict);
        fence.Setup(value => value.ReleaseAsync(CancellationToken.None)).ThrowsAsync(new IOException("fence-release-failed"));
        HostUpdateExecutionStepsAdapter adapter = CreateAdapter(store, verifier.Object, fence.Object);

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            adapter.VerifyAsync(request, CancellationToken.None));

        Assert.Equal("fence-release-failed", exception.Message);
        await AssertPersistedStateAsync(path, request);
    }

    private static HostUpdateExecutionStepsAdapter CreateAdapter(
        IInstalledHostStateStore store,
        IHostUpdateHealthVerifier verifier,
        IHostUpdateFenceCoordinator fence) => new(
            new Mock<IHostUpdatePreflightCheck>(MockBehavior.Strict).Object,
            new Mock<IHostUpdateDrainCoordinator>(MockBehavior.Strict).Object,
            fence,
            new Mock<IHostUpdateBackupCoordinator>(MockBehavior.Strict).Object,
            new Mock<IHostUpdateMigrationCoordinator>(MockBehavior.Strict).Object,
            new Mock<IHostUpdateApplyCoordinator>(MockBehavior.Strict).Object,
            verifier,
            store);

    private static HostUpdateExecutionRequest Request(string platform) => new(
        "stable:1.2.3",
        2,
        "sha256:" + new string('a', 64),
        new string('b', 40),
        HostUpdateExecutionChannel.Stable,
        HostUpdateExecutionRequest.RequiredServiceIds.OrderByDescending(id => id, StringComparer.Ordinal)
            .Select((id, index) => new HostUpdateExecutionTarget(id, platform, "sha256:" + new string((char)('0' + index), 64)))
            .ToArray())
        {
            RequestId = "request-1",
            TrustRoot = "default",
            PolicyRevision = 1,
            PolicyFingerprint = "policy-1",
            HostPlatform = platform,
            AuthorizationKind = HostUpdateAuthorizationKind.Manual,
        };

    private static Task WritePriorStateAsync(FileInstalledHostStateStore store) => store.WriteAsync(
        new InstalledHostState(
            "stable:1.2.2",
            "sha256:" + new string('c', 64),
            new Dictionary<string, string> { ["api"] = "sha256:" + new string('d', 64) },
            "api",
            DateTimeOffset.UtcNow.AddDays(-1),
            new Dictionary<string, string> { ["api"] = "linux-amd64" }),
        CancellationToken.None);

    private static async Task AssertPersistedStateAsync(string path, HostUpdateExecutionRequest request)
    {
        InstalledHostState state = Assert.IsType<InstalledHostState>(
            await new FileInstalledHostStateStore(path).ReadAsync(CancellationToken.None));
        Assert.Equal(request.ReleaseId, state.ReleaseId);
        Assert.Equal(request.ManifestDigest, state.ManifestDigest);
        Assert.Equal(string.Join('+', request.Targets.Select(target => target.ServiceId).OrderBy(id => id, StringComparer.Ordinal)), state.Topology);
        Assert.Equal(request.Targets.Count, state.ServiceDigests.Count);
        Assert.NotNull(state.ServicePlatforms);
        Assert.Equal(request.Targets.Count, state.ServicePlatforms.Count);
        foreach (HostUpdateExecutionTarget target in request.Targets)
        {
            Assert.Equal(target.ChildDigest, state.ServiceDigests[target.ServiceId]);
            Assert.Equal(target.Platform, state.ServicePlatforms[target.ServiceId]);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
