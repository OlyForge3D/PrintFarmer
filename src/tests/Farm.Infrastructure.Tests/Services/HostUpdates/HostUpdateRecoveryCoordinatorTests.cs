using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Kane audit P1.7: a <see cref="HostUpdateRecoveryOutcome.NeedsOperator"/> (or any other) recovery
/// outcome must be durably persisted by the coordinator itself, not merely returned to a caller that
/// might crash before it gets a chance to persist it -- and must be readable again after a fresh
/// process/store instance (restart survival).
/// </summary>
public sealed class HostUpdateRecoveryCoordinatorTests
{
    private static readonly HostUpdateExecutionRequest Request = new(
        "release-1", 1, "sha256:manifest", "abc123", HostUpdateExecutionChannel.Stable,
        [new HostUpdateExecutionTarget("api", "linux/amd64", "sha256:api")]);

    private static readonly IReadOnlyList<HostUpdateExecutionActivity> NoActivities = [];

    [Fact]
    public async Task RecoverAsync_ImageOnlyRollbackSucceeds_PersistsRolledBackOutcome()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var installedStateStore = new FakeInstalledHostStateStore(new InstalledHostState(
            "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow));
        var coordinator = new HostUpdateRecoveryCoordinator(
            installedStateStore,
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
    }

    [Fact]
    public async Task RecoverAsync_NoBackupAvailable_PersistsNeedsOperatorOutcome()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(installedState: null),
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        result.Detail.Should().Be("no_backup_available");
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted!.Detail.Should().Be("no_backup_available");
    }

    [Fact]
    public async Task RecoverAsync_ExceptionDuringRecovery_PersistsNeedsOperatorFailClosed()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var installedStateStore = new FakeInstalledHostStateStore(new InstalledHostState(
            "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow));
        var coordinator = new HostUpdateRecoveryCoordinator(
            installedStateStore,
            new AlwaysCompatibleEvaluator(),
            new ThrowingDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
    }

    [Fact]
    public async Task RecoverAsync_CancelledMidFlight_StillPersistsNeedsOperatorBeforeRethrow()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var installedStateStore = new FakeInstalledHostStateStore(new InstalledHostState(
            "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow));
        var coordinator = new HostUpdateRecoveryCoordinator(
            installedStateStore,
            new AlwaysCompatibleEvaluator(),
            new CancelingDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);

        Func<Task> act = () => coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        persisted.Detail.Should().Be("recovery_canceled");
    }

    [Fact]
    public async Task RecoverAsync_RequestFingerprintMismatch_PersistsNeedsOperatorAndNeverStartsRollback()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        HostUpdateExecutionRequest original = Request;
        HostUpdateExecutionRequest tampered = original with { SourceCommit = "def456" };
        IReadOnlyList<HostUpdateExecutionActivity> activities =
        [
            new HostUpdateExecutionActivity(
                "accepted",
                original.ReleaseId,
                HostUpdateExecutionState.Accepted,
                "accepted",
                DateTimeOffset.UtcNow,
                HostUpdateRequestFingerprint.Compute(original)),
        ];
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(new InstalledHostState(
                "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow)),
            new AlwaysCompatibleEvaluator(),
            new ThrowingDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(tampered, activities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        result.Detail.Should().Be("request_fingerprint_mismatch");
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(original.ReleaseId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        persisted.Detail.Should().Be("request_fingerprint_mismatch");
    }
    [Fact]
    public async Task OutcomeStore_SurvivesFreshInstanceAfterRestart()
    {
        string root = CreateTempDir();
        var firstInstance = new FileHostUpdateRecoveryOutcomeStore(root);
        await firstInstance.WriteAsync(
            new HostUpdateRecoveryOutcomeRecord("release-x", HostUpdateRecoveryOutcome.NeedsOperator, "no_backup_available", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var secondInstance = new FileHostUpdateRecoveryOutcomeStore(root);
        HostUpdateRecoveryOutcomeRecord? read = await secondInstance.ReadAsync("release-x", CancellationToken.None);

        read.Should().NotBeNull();
        read!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
    }

    [Fact]
    public async Task RecoverAsync_RolledBackOutcomeIsPersistedBeforeFenceRelease()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var fence = new RecordingFenceCoordinator(async () =>
        {
            HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
            persisted.Should().NotBeNull();
            persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        });
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(new InstalledHostState(
                "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow)),
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            fence);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        fence.ReleaseCount.Should().Be(1);
    }

    [Fact]
    public async Task RecoverAsync_FenceReleaseFails_PersistsNeedsOperatorAndReturnsClosedOutcome()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var fence = new RecordingFenceCoordinator(() => throw new InvalidOperationException("release_failed"));
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(new InstalledHostState(
                "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow)),
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            fence);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        result.Detail.Should().Be("fence_release_failed:InvalidOperationException");
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        persisted.Detail.Should().Be("fence_release_failed:InvalidOperationException");
    }
    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "pf-recovery-outcome-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }


    private sealed class RecordingFenceCoordinator(Func<Task> onRelease) : IHostUpdateFenceCoordinator
    {
        public int ReleaseCount { get; private set; }

        public RecordingFenceCoordinator(Action onRelease)
            : this(() => { onRelease(); return Task.CompletedTask; })
        {
        }

        public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            ReleaseCount++;
            await onRelease().ConfigureAwait(false);
        }
    }

    private sealed class FakeInstalledHostStateStore(InstalledHostState? installedState) : IInstalledHostStateStore
    {
        public Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(installedState);

        public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlwaysCompatibleEvaluator : IHostUpdateRecoveryCompatibilityEvaluator
    {
        public bool SupportsImageOnlyRollback(InstalledHostState priorState, IReadOnlyList<HostUpdateExecutionActivity> activities) => true;
    }

    private sealed class FakeDigestApplier : IHostUpdateDigestApplier
    {
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingDigestApplier : IHostUpdateDigestApplier
    {
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("apply_failed");
    }

    private sealed class CancelingDigestApplier : IHostUpdateDigestApplier
    {
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class FakeDigestVerifier : IHostUpdateDigestVerifier
    {
        public Task VerifyDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NeverInvokedRestoreExecutor : IHostUpdateRestoreExecutor
    {
        public Task RestoreAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("should not be invoked when image-only rollback is compatible");
    }

    private sealed class NeverFindsManifestLocator : IHostUpdateBackupManifestLocator
    {
        public Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken) =>
            Task.FromResult<(HostUpdateBackupManifest, string)?>(null);
    }
}
