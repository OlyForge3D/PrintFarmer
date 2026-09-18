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
    public async Task RecoverAsync_AcquiresExecutionLockAcrossValidationApplyAndOutcome()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var lockProbe = new RecordingExecutionLock(() => outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None).Result.Should().BeNull());
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(new InstalledHostState(
                "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow)),
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            executionLock: lockProbe);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        lockProbe.AcquireCount.Should().Be(1);
        lockProbe.Disposed.Should().BeTrue();
    }


    [Fact]
    public async Task RecoverAsync_ExistingRolledBackOutcome_DoesNotReplayRestoreOrApply()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        await outcomeStore.WriteAsync(
            new HostUpdateRecoveryOutcomeRecord(Request.ReleaseId, HostUpdateRecoveryOutcome.RolledBack, "coordinated_restore", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(new InstalledHostState(
                "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow)),
            new AlwaysCompatibleEvaluator(),
            new ThrowingDigestApplier(),
            new ThrowingRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            new RecordingFenceCoordinator(() => { }),
            executionLock: new RecordingExecutionLock(() => { }));

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        result.Detail.Should().Be("coordinated_restore");
    }

    [Fact]
    public async Task RecoverAsync_ApplyMayHaveStartedWithoutPriorImageState_PersistsNeedsOperator()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        IReadOnlyList<HostUpdateExecutionActivity> activities =
        [
            new HostUpdateExecutionActivity("accepted", Request.ReleaseId, HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow, HostUpdateRequestFingerprint.Compute(Request)),
            new HostUpdateExecutionActivity("apply-before", Request.ReleaseId, HostUpdateExecutionState.Applying, "apply:before", DateTimeOffset.UtcNow, HostUpdateRequestFingerprint.Compute(Request)),
        ];
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(installedState: null),
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new ThrowingRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, activities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        result.Detail.Should().Be("prior_image_state_missing_after_apply_started");
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        persisted.Detail.Should().Be("prior_image_state_missing_after_apply_started");
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
    public async Task RecoverAsync_FenceReleasePendingIsPersistedBeforeFenceRelease()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var fence = new RecordingFenceCoordinator(async () =>
        {
            // The durable state observed during the release window must be fence-release-pending,
            // never a plain terminal RolledBack: a crash here would otherwise be read as fully
            // resolved and strand admission closed forever.
            HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
            persisted.Should().NotBeNull();
            persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
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
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        persisted.Detail.Should().NotContain("fence_release");
    }

    [Fact]
    public async Task RecoverAsync_CrashAfterFirstPostRollbackWrite_ReDrivesReleaseOnly()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var crashingFence = new RecordingFenceCoordinator(() => throw new InvalidOperationException("killed"));
        var installedState = new FakeInstalledHostStateStore(new InstalledHostState(
            "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow));

        // First attempt: rollback succeeds, then the process dies during fence release.
        HostUpdateRecoveryResult first = await new HostUpdateRecoveryCoordinator(
            installedState,
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            crashingFence).RecoverAsync(Request, NoActivities, CancellationToken.None);

        first.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);

        // Restart: the surviving checkpoint must re-drive the idempotent release alone -- never
        // restore, never digest apply.
        var restore = new TrackingRestoreExecutor();
        var applier = new ThrowingDigestApplier();
        var retryFence = new RecordingFenceCoordinator(() => { });
        HostUpdateRecoveryResult second = await new HostUpdateRecoveryCoordinator(
            installedState,
            new AlwaysCompatibleEvaluator(),
            applier,
            restore,
            new FindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            retryFence).RecoverAsync(Request, NoActivities, CancellationToken.None);

        second.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        retryFence.ReleaseCount.Should().Be(1);
        restore.Called.Should().BeFalse();
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
    }

    [Fact]
    public async Task RecoverAsync_FenceReleaseFails_PreservesFenceReleasePendingWithDiagnostic()
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

        // The rollback is complete and the fence is still closed, so release-only restart semantics
        // must be preserved instead of being flattened into a generic NeedsOperator.
        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        result.Detail.Should().EndWith("|fence_release_failed:InvalidOperationException");
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        persisted.Detail.Should().EndWith("|fence_release_failed:InvalidOperationException");
    }

    [Fact]
    public async Task RecoverAsync_RepeatedFenceReleaseFailures_DoNotAccumulateDiagnosticSuffixes()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var installedState = new FakeInstalledHostStateStore(new InstalledHostState(
            "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow));
        HostUpdateRecoveryCoordinator Failing() => new(
            installedState,
            new AlwaysCompatibleEvaluator(),
            new ThrowingDigestApplier(),
            new TrackingRestoreExecutor(),
            new FindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            new RecordingFenceCoordinator(() => throw new InvalidOperationException("release_failed")));

        await new HostUpdateRecoveryCoordinator(
            installedState,
            new AlwaysCompatibleEvaluator(),
            new FakeDigestApplier(),
            new NeverInvokedRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            new RecordingFenceCoordinator(() => throw new InvalidOperationException("release_failed"))).RecoverAsync(Request, NoActivities, CancellationToken.None);
        HostUpdateRecoveryResult second = await Failing().RecoverAsync(Request, NoActivities, CancellationToken.None);

        second.Detail.Split('|').Should().HaveCount(2);
    }

    [Fact]
    public async Task RecoverAsync_FenceReleasePendingOutcome_NeverReplaysRestoreOrApply()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        await outcomeStore.WriteAsync(
            new HostUpdateRecoveryOutcomeRecord(Request.ReleaseId, HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore|fence_release_failed:IOException", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var fence = new RecordingFenceCoordinator(() => { });
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(installedState: null),
            new AlwaysCompatibleEvaluator(),
            new ThrowingDigestApplier(),
            new ThrowingRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore,
            fence);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        result.Detail.Should().Be("coordinated_restore");
        fence.ReleaseCount.Should().Be(1);
    }

    [Fact]
    public async Task RecoverAsync_CompletedInstallationRefusesDestructiveRestore()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        var restore = new TrackingRestoreExecutor();
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FakeInstalledHostStateStore(new InstalledHostState(
                "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow)),
            new NeverCompatibleEvaluator(),
            new FakeDigestApplier(),
            restore,
            new FindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);
        IReadOnlyList<HostUpdateExecutionActivity> activities =
        [
            new("activity", Request.ReleaseId, HostUpdateExecutionState.Completed, "installed-state:after", DateTimeOffset.UtcNow, HostUpdateRequestFingerprint.Compute(Request)),
        ];

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, activities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        result.Detail.Should().Be("completed_installation_requires_fence_release");
        restore.Called.Should().BeFalse();
    }

    [Fact]
    public async Task RestoreAsync_UnmappedManifestTarget_ThrowsFailClosed()
    {
        var runner = new RecordingProcessRunner();
        var restore = new ProcessHostUpdateRestoreExecutor(runner, new Dictionary<string, Func<string, HostUpdateRestoreCommand>>(), new Dictionary<string, string>(), TimeSpan.FromSeconds(30));
        string root = CreateTempDir();
        string target = Path.Combine(root, "unknown");
        Directory.CreateDirectory(target);
        string file = Path.Combine(target, "payload.txt");
        await File.WriteAllTextAsync(file, "data");
        var manifest = new HostUpdateBackupManifest("release-1", DateTimeOffset.UtcNow, ["unknown"], [new HostUpdateBackupManifestFile(Path.Combine("unknown", "payload.txt"), Sha256("data"), 4)]);

        Func<Task> act = () => restore.RestoreAsync(manifest, root, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*restore_target_unmapped:unknown*");
    }

    [Fact]
    public async Task RecoverAsync_CorruptInstalledState_FailsClosedToNeedsOperator()
    {
        string root = CreateTempDir();
        var outcomeStore = new FileHostUpdateRecoveryOutcomeStore(root);
        string statePath = Path.Combine(root, "installed-state.json");
        await File.WriteAllTextAsync(statePath, "{}", CancellationToken.None);
        var restore = new TrackingRestoreExecutor();
        var coordinator = new HostUpdateRecoveryCoordinator(
            new FileInstalledHostStateStore(statePath),
            new AlwaysCompatibleEvaluator(),
            new ThrowingDigestApplier(),
            restore,
            new FindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomeStore);

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(Request, NoActivities, CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        restore.Called.Should().BeFalse();
        HostUpdateRecoveryOutcomeRecord? persisted = await outcomeStore.ReadAsync(Request.ReleaseId, CancellationToken.None);
        persisted!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
    }

    private sealed class RecordingProcessRunner : IHostUpdateProcessRunner
    {
        public Task<HostUpdateProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new HostUpdateProcessResult(0, string.Empty, string.Empty));
    }

    private sealed class TrackingRestoreExecutor : IHostUpdateRestoreExecutor
    {
        public bool Called { get; private set; }

        public Task RestoreAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class NeverCompatibleEvaluator : IHostUpdateRecoveryCompatibilityEvaluator
    {
        public bool SupportsImageOnlyRollback(InstalledHostState priorState, IReadOnlyList<HostUpdateExecutionActivity> activities) => false;
    }

    private sealed class FindsManifestLocator : IHostUpdateBackupManifestLocator
    {
        public Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken) =>
            Task.FromResult<(HostUpdateBackupManifest Manifest, string RunDirectory)?>((
                new HostUpdateBackupManifest(releaseId, DateTimeOffset.UtcNow, ["api"], []),
                Path.GetTempPath()));
    }

    private static string Sha256(string text) =>
        "sha256".Replace("sha256", string.Empty) + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "pf-recovery-outcome-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }



    private sealed class RecordingExecutionLock(Action onAcquire) : IHostUpdateExecutionLock
    {
        public int AcquireCount { get; private set; }

        public bool Disposed { get; private set; }

        public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken)
        {
            AcquireCount++;
            onAcquire();
            return new Lease(this);
        }

        private sealed class Lease(RecordingExecutionLock owner) : IHostUpdateExecutionLease
        {
            public void Dispose() => owner.Disposed = true;
        }
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
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? platformsByService = null) => Task.CompletedTask;
    }

    private sealed class ThrowingDigestApplier : IHostUpdateDigestApplier
    {
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? platformsByService = null) =>
            throw new InvalidOperationException("apply_failed");
    }

    private sealed class CancelingDigestApplier : IHostUpdateDigestApplier
    {
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? platformsByService = null) =>
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

    private sealed class ThrowingRestoreExecutor : IHostUpdateRestoreExecutor
    {
        public Task RestoreAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("restore_replayed");
    }

    private sealed class NeverFindsManifestLocator : IHostUpdateBackupManifestLocator
    {
        public Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken) =>
            Task.FromResult<(HostUpdateBackupManifest, string)?>(null);
    }
}
