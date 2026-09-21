using Farm.Infrastructure.Services.HostUpdates;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutionStepsAdapterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hu-steps-").FullName;

    [Theory]
    [InlineData("linux-amd64", "stable:1.2.3", HostUpdateExecutionChannel.Stable)]
    [InlineData("linux-arm64", "stable:1.2.3", HostUpdateExecutionChannel.Stable)]
    [InlineData("linux-amd64", "insider:1.2.3-insider.4", HostUpdateExecutionChannel.Insider)]
    public async Task VerifyAsync_VerifiedTargets_PersistsCompleteStateBeforeReleasingFence(
        string platform, string releaseId, HostUpdateExecutionChannel channel)
    {
        HostUpdateExecutionRequest request = Request(platform) with { ReleaseId = releaseId, Channel = channel };
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
    public async Task VerifyAsync_AtomicWritePathTooLong_PreservesPriorStateAndKeepsFenceClosed()
    {
        HostUpdateExecutionRequest request = Request("linux-amd64");
        Assert.True(request.IsValid(out string error), error);
        string priorPath = Path.Combine(_root, "prior-state.json");
        await WritePriorStateAsync(new FileInstalledHostStateStore(priorPath));
        // The prior filename fits NTFS/ext4 limits; the atomic writer's GUID suffix does not.
        string path = Path.Combine(_root, new string('s', 240) + ".json");
        File.Move(priorPath, path);
        var store = new FileInstalledHostStateStore(path);
        Assert.NotNull(await store.ReadAsync(CancellationToken.None));
        string prior = await File.ReadAllTextAsync(path);
        var verifier = new Mock<IHostUpdateHealthVerifier>(MockBehavior.Strict);
        verifier.Setup(value => value.RunAsync(request, CancellationToken.None)).Returns(Task.CompletedTask);
        var fence = new Mock<IHostUpdateFenceCoordinator>(MockBehavior.Strict);
        HostUpdateExecutionStepsAdapter adapter = CreateAdapter(store, verifier.Object, fence.Object);

        await Assert.ThrowsAnyAsync<IOException>(() =>
                adapter.VerifyAsync(request, CancellationToken.None));

        Assert.Equal(prior, await File.ReadAllTextAsync(path));
        fence.Verify(value => value.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("manifest", "sha256:", 'A', 64, "release_binding_invalid")]
    [InlineData("manifest", "SHA256:", 'a', 64, "release_binding_invalid")]
    [InlineData("manifest", "sha256:", 'a', 63, "release_binding_invalid")]
    [InlineData("manifest", "sha256:", 'z', 64, "release_binding_invalid")]
    [InlineData("platform", "sha256:", 'A', 64, "target_invalid")]
    [InlineData("platform", "SHA256:", 'a', 64, "target_invalid")]
    [InlineData("platform", "sha256:", 'a', 63, "target_invalid")]
    [InlineData("platform", "sha256:", 'z', 64, "target_invalid")]
    public async Task ExecuteAsync_NoncanonicalDigest_RejectsBeforeAnySideEffect(
        string field, string prefix, char character, int length, string reason)
    {
        HostUpdateExecutionRequest request = Request("linux-amd64");
        string digest = prefix + new string(character, length);
        request = field == "manifest"
            ? request with { ManifestDigest = digest }
            : request with { Targets = request.Targets.Select(target => target with { ChildDigest = digest }).ToArray() };

        await AssertRejectedBeforeSideEffectsAsync(request, reason);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("platform")]
    public async Task ExecuteAsync_MixedCaseDigest_RejectsBeforeAnySideEffect(string field)
    {
        HostUpdateExecutionRequest request = Request("linux-amd64");
        string digest = "sha256:" + new string('a', 63) + "B";
        request = field == "manifest"
            ? request with { ManifestDigest = digest }
            : request with { Targets = request.Targets.Select(target => target with { ChildDigest = digest }).ToArray() };

        await AssertRejectedBeforeSideEffectsAsync(request, field == "manifest" ? "release_binding_invalid" : "target_invalid");
    }

    [Theory]
    [InlineData("rel-1")]
    [InlineData("stable:01.2.3")]
    [InlineData("stable:1.2.3-insider.4")]
    [InlineData("insider:1.2.3")]
    [InlineData("unknown:1.2.3")]
    public async Task ExecuteAsync_NoncanonicalReleaseId_RejectsBeforeAnySideEffect(string releaseId)
    {
        await AssertRejectedBeforeSideEffectsAsync(Request("linux-amd64") with { ReleaseId = releaseId }, "release_binding_invalid");
    }

    private static async Task AssertRejectedBeforeSideEffectsAsync(HostUpdateExecutionRequest request, string reason)
    {
        var steps = new Mock<IHostUpdateExecutionSteps>(MockBehavior.Strict);
        var journal = new Mock<IHostUpdateExecutionJournal>(MockBehavior.Strict);
        var updateLock = new Mock<IHostUpdateExecutionLock>(MockBehavior.Strict);
        var policy = new Mock<IHostUpdateAutomationPolicyRepository>(MockBehavior.Strict);
        var executor = new HostUpdateExecutor(steps.Object, journal.Object, updateLock.Object, policy.Object);

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(reason, result.FailureCode);
        Assert.Empty(result.Activities);
        steps.VerifyNoOtherCalls();
        journal.VerifyNoOtherCalls();
        updateLock.VerifyNoOtherCalls();
        policy.VerifyNoOtherCalls();
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
