using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Issue #2823: policy drift observed on a resumed release that already fenced writers must leave a
/// reachable recovery path that releases the fence, without replaying migration, apply or restore.
/// </summary>
public sealed class HostUpdatePolicyDriftFenceRecoveryTests
{
    private static HostUpdateAutomationPolicy Policy() => new(Enabled: true, Revision: 1);

    private static HostUpdateAutomationPolicy DriftedPolicy() => Policy() with { PollIntervalSeconds = 1200 };

    private static HostUpdateExecutionRequest Request() => new("stable:1.2.3", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, [
        new("api", "linux-amd64", "sha256:" + new string('a', 64)),
        new("frontend", "linux-amd64", "sha256:" + new string('b', 64)),
        new("slicer-host", "linux-amd64", "sha256:" + new string('c', 64)),
        new("printer-discovery", "linux-amd64", "sha256:" + new string('d', 64)),
        new("orcaslicer-worker", "linux-amd64", "sha256:" + new string('e', 64)),
        new("monolith", "linux-amd64", "sha256:" + new string('f', 64)),
    ])
    {
        RequestId = "request-1",
        TrustRoot = "root-1",
        PolicyRevision = 1,
        PolicyFingerprint = HostStateHostUpdateSchedulerSettings.ToSchedulerSettings(Policy()).Fingerprint,
        HostPlatform = "linux-amd64",
    };

    [Fact]
    public async Task PolicyDriftAfterFence_RecoveryReleasesFenceWithoutReplayingSideEffects()
    {
        var journal = new MemoryJournal();
        var writer = new TestWriter("api-admission");
        var fence = new HostUpdateFenceCoordinator([writer], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        HostUpdateExecutionResult interrupted = await RunUntilAsync("fence", journal, fence);

        interrupted.State.Should().Be(HostUpdateExecutionState.Fenced);
        interrupted.FailureCode.Should().Be("canceled");
        writer.Quiesced.Should().BeTrue();

        var resumedSteps = new RecordingSteps();
        HostUpdateExecutionResult drifted = await new HostUpdateExecutor(resumedSteps, journal, new NoopLock(), new InlinePolicyRepository(DriftedPolicy()))
            .ExecuteAsync(Request());

        drifted.State.Should().Be(HostUpdateExecutionState.RecoveryRequired);
        drifted.FailureCode.Should().Be("policy_drifted");
        resumedSteps.Calls.Should().BeEmpty();
        HostUpdateExecutionActivity marker = journal.Read(Request().ReleaseId)[^1];
        marker.State.Should().Be(HostUpdateExecutionState.RecoveryRequired);
        marker.Phase.Should().Be("failure:policy_drifted");
        marker.RequestBindingHash.Should().Be(HostUpdateRequestBinding.Compute(Request()));
        drifted.Activities.Should().EndWith(marker);

        var outcomeStore = new MemoryOutcomeStore();
        var sideEffects = new SideEffectRecorder();
        HostUpdateRecoveryCoordinator inner = Coordinator(outcomeStore, sideEffects, fence);
        HostUpdateRecoveryPlan plan = await inner.PlanAsync(Request(), journal.Read(Request().ReleaseId), CancellationToken.None);
        plan.Kind.Should().Be(HostUpdateRecoveryPlanKind.FenceReleaseOnly);
        plan.Detail.Should().Be("policy_drifted_before_side_effects");

        HostUpdateRecoveryResult recovered = await new JournaledHostUpdateRecoveryCoordinator(journal, new NoopRecoveryLeaseProvider(), inner)
            .RecoverAsync(Request(), journal.Read(Request().ReleaseId), CancellationToken.None);

        recovered.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        recovered.Detail.Should().Be("policy_drifted_before_side_effects");
        writer.Quiesced.Should().BeFalse("writers must resume once recovery releases the fence");
        writer.ResumeCount.Should().Be(1);
        sideEffects.Calls.Should().BeEmpty();
        outcomeStore.Writes.Select(w => w.Outcome).Should().Equal(HostUpdateRecoveryOutcome.FenceReleasePending, HostUpdateRecoveryOutcome.RolledBack);
        journal.Read(Request().ReleaseId)[^1].Phase.Should().Be("recovery:rolled_back");

        HostUpdateRecoveryResult repeated = await new JournaledHostUpdateRecoveryCoordinator(journal, new NoopRecoveryLeaseProvider(), inner)
            .RecoverAsync(Request(), journal.Read(Request().ReleaseId), CancellationToken.None);
        repeated.Detail.Should().Be("not_in_recovery");
        writer.ResumeCount.Should().Be(1);
    }

    [Fact]
    public async Task PolicyDriftAfterFence_PhysicalReconciliationPending_KeepsFenceReleasePendingAndWritersFenced()
    {
        var journal = new MemoryJournal();
        var writer = new TestWriter("api-admission");
        var fence = new HostUpdateFenceCoordinator([writer], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        await RunUntilAsync("fence", journal, fence);
        await new HostUpdateExecutor(new RecordingSteps(), journal, new NoopLock(), new InlinePolicyRepository(DriftedPolicy())).ExecuteAsync(Request());

        var outcomeStore = new MemoryOutcomeStore();
        var sideEffects = new SideEffectRecorder();
        var gate = new StaticReconciliationGate { Recorded = false };
        HostUpdateRecoveryCoordinator inner = Coordinator(outcomeStore, sideEffects, fence, gate);
        var journaled = new JournaledHostUpdateRecoveryCoordinator(journal, new NoopRecoveryLeaseProvider(), inner);

        HostUpdateRecoveryResult blocked = await journaled.RecoverAsync(Request(), journal.Read(Request().ReleaseId), CancellationToken.None);

        blocked.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        blocked.Detail.Should().Be("policy_drifted_before_side_effects|" + HostUpdatePhysicalReconciliationCodes.Pending);
        writer.Quiesced.Should().BeTrue();
        journal.Read(Request().ReleaseId)[^1].State.Should().Be(HostUpdateExecutionState.RecoveryRequired);

        gate.Recorded = true;
        HostUpdateRecoveryResult released = await journaled.RecoverAsync(Request(), journal.Read(Request().ReleaseId), CancellationToken.None);

        released.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        writer.Quiesced.Should().BeFalse();
        sideEffects.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyDriftBeforeFence_KeepsJournalUntouchedAndResumesAfterDriftClears()
    {
        var journal = new MemoryJournal();
        var writer = new TestWriter("api-admission");
        var fence = new HostUpdateFenceCoordinator([writer], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        await RunUntilAsync("drain", journal, fence);
        int journaledBeforeDrift = journal.Read(Request().ReleaseId).Count;

        HostUpdateExecutionResult drifted = await new HostUpdateExecutor(new RecordingSteps(), journal, new NoopLock(), new InlinePolicyRepository(DriftedPolicy()))
            .ExecuteAsync(Request());

        drifted.State.Should().Be(HostUpdateExecutionState.RecoveryRequired);
        drifted.FailureCode.Should().Be("policy_drifted");
        drifted.Activities.Should().BeEmpty();
        journal.Read(Request().ReleaseId).Should().HaveCount(journaledBeforeDrift);
        writer.Quiesced.Should().BeFalse();

        HostUpdateRecoveryResult recovery = await new JournaledHostUpdateRecoveryCoordinator(
            journal,
            new NoopRecoveryLeaseProvider(),
            Coordinator(new MemoryOutcomeStore(), new SideEffectRecorder(), fence))
            .RecoverAsync(Request(), journal.Read(Request().ReleaseId), CancellationToken.None);
        recovery.Detail.Should().Be("not_in_recovery");

        var resumedSteps = new RecordingSteps(fence);
        HostUpdateExecutionResult resumed = await new HostUpdateExecutor(resumedSteps, journal, new NoopLock(), new InlinePolicyRepository(Policy()))
            .ExecuteAsync(Request());

        resumed.State.Should().Be(HostUpdateExecutionState.Completed);
        resumedSteps.Calls.Should().Equal("fence", "backup", "migration", "apply", "verify");
    }

    [Fact]
    public async Task PolicyDriftOnBrandNewRequest_WritesNoJournalEntry()
    {
        var journal = new MemoryJournal();

        HostUpdateExecutionResult drifted = await new HostUpdateExecutor(new RecordingSteps(), journal, new NoopLock(), new InlinePolicyRepository(DriftedPolicy()))
            .ExecuteAsync(Request());

        drifted.FailureCode.Should().Be("policy_drifted");
        journal.ListReleaseIds().Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyDriftAfterUnsafeSideEffectStarted_StaysFailClosedAndFenced()
    {
        HostUpdateExecutionRequest request = Request();
        var journal = new MemoryJournal();
        foreach ((HostUpdateExecutionState state, string phase) in new[]
        {
            (HostUpdateExecutionState.Accepted, "accepted"),
            (HostUpdateExecutionState.Preflight, "preflight:before"),
            (HostUpdateExecutionState.Preflight, "preflight:after"),
            (HostUpdateExecutionState.Draining, "drain:before"),
            (HostUpdateExecutionState.Draining, "drain:after"),
            (HostUpdateExecutionState.Fenced, "fence:before"),
            (HostUpdateExecutionState.Fenced, "fence:after"),
            (HostUpdateExecutionState.BackedUp, "backup:before"),
            (HostUpdateExecutionState.BackedUp, "backup:after"),
            (HostUpdateExecutionState.Migrating, "migration:before"),
        })
        {
            journal.Append(Bound(request, state, phase));
        }

        var writer = new TestWriter("api-admission");
        var fence = new HostUpdateFenceCoordinator([writer], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        await fence.RunAsync(request, CancellationToken.None);

        HostUpdateExecutionResult drifted = await new HostUpdateExecutor(new RecordingSteps(), journal, new NoopLock(), new InlinePolicyRepository(DriftedPolicy()))
            .ExecuteAsync(request);

        drifted.FailureCode.Should().Be("policy_drifted");
        journal.Read(request.ReleaseId)[^1].Phase.Should().Be("failure:policy_drifted");

        var sideEffects = new SideEffectRecorder();
        HostUpdateRecoveryResult recovery = await new JournaledHostUpdateRecoveryCoordinator(
            journal,
            new NoopRecoveryLeaseProvider(),
            Coordinator(new MemoryOutcomeStore(), sideEffects, fence))
            .RecoverAsync(request, journal.Read(request.ReleaseId), CancellationToken.None);

        recovery.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        recovery.Detail.Should().Be("no_backup_available");
        writer.Quiesced.Should().BeTrue("uncertain migration must keep writers fenced for an operator");
        sideEffects.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyDriftWhileAlreadyInRecovery_DoesNotAppendDuplicateMarker()
    {
        var journal = new MemoryJournal();
        var fence = new HostUpdateFenceCoordinator([new TestWriter("api-admission")], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        await RunUntilAsync("fence", journal, fence);
        var executor = new HostUpdateExecutor(new RecordingSteps(), journal, new NoopLock(), new InlinePolicyRepository(DriftedPolicy()));
        await executor.ExecuteAsync(Request());
        int afterFirstDrift = journal.Read(Request().ReleaseId).Count;

        HostUpdateExecutionResult second = await executor.ExecuteAsync(Request());

        second.FailureCode.Should().Be("policy_drifted");
        journal.Read(Request().ReleaseId).Should().HaveCount(afterFirstDrift);
    }

    [Fact]
    public async Task PolicyDriftWithForeignRequestBinding_DoesNotAppendMarker()
    {
        var journal = new MemoryJournal();
        var fence = new HostUpdateFenceCoordinator([new TestWriter("api-admission")], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        await RunUntilAsync("fence", journal, fence);
        int journaled = journal.Read(Request().ReleaseId).Count;
        HostUpdateExecutionRequest other = Request() with { RequestId = "request-2" };

        HostUpdateExecutionResult drifted = await new HostUpdateExecutor(new RecordingSteps(), journal, new NoopLock(), new InlinePolicyRepository(DriftedPolicy()))
            .ExecuteAsync(other);

        drifted.FailureCode.Should().Be("policy_drifted");
        drifted.Activities.Should().BeEmpty();
        journal.Read(Request().ReleaseId).Should().HaveCount(journaled);
    }

    private static async Task<HostUpdateExecutionResult> RunUntilAsync(string stopAfterPhase, MemoryJournal journal, IHostUpdateFenceCoordinator fence)
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new RecordingSteps(fence, stopAfterPhase, cancellation);
        return await new HostUpdateExecutor(steps, journal, new NoopLock(), new InlinePolicyRepository(Policy()))
            .ExecuteAsync(Request(), cancellation.Token);
    }

    private static HostUpdateExecutionActivity Bound(HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase) =>
        new(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        };

    private static HostUpdateRecoveryCoordinator Coordinator(
        MemoryOutcomeStore outcomeStore,
        SideEffectRecorder sideEffects,
        IHostUpdateFenceCoordinator fence,
        IHostUpdatePhysicalReconciliationGate? gate = null) =>
        new(
            new StaticInstalledStateStore(new InstalledHostState(
                "stable:1.2.2", "sha256:" + new string('9', 64), new Dictionary<string, string> { ["api"] = "sha256:" + new string('8', 64) }, "monolith", DateTimeOffset.UtcNow)),
            new DefaultHostUpdateRecoveryCompatibilityEvaluator(),
            sideEffects,
            sideEffects,
            new NoBackupLocator(),
            sideEffects,
            outcomeStore,
            fence,
            physicalReconciliationGate: gate);

    private sealed class TestWriter(string name) : IFenceableWriter
    {
        public string Name { get; } = name;

        public bool Quiesced { get; private set; }

        public int ResumeCount { get; private set; }

        public Task QuiesceAsync(CancellationToken cancellationToken)
        {
            Quiesced = true;
            return Task.CompletedTask;
        }

        public Task<bool> IsQuiescedAsync(CancellationToken cancellationToken) => Task.FromResult(Quiesced);

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            Quiesced = false;
            ResumeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSteps(
        IHostUpdateFenceCoordinator? fence = null,
        string? stopAfterPhase = null,
        CancellationTokenSource? cancellation = null) : IHostUpdateExecutionSteps
    {
        public List<string> Calls { get; } = [];

        public Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct) => RecordAsync("preflight");

        public Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct) => RecordAsync("drain");

        public async Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct)
        {
            if (fence is not null)
            {
                await fence.RunAsync(request, ct);
            }

            await RecordAsync("fence");
        }

        public Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct) => RecordAsync("backup");

        public Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct) => RecordAsync("migration");

        public Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => RecordAsync("apply");

        public Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => RecordAsync("verify");

        private Task RecordAsync(string phase)
        {
            Calls.Add(phase);
            if (string.Equals(phase, stopAfterPhase, StringComparison.Ordinal))
            {
                // Simulates a safe-checkpoint interruption; a later execution resumes from the journal.
                cancellation?.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SideEffectRecorder : IHostUpdateDigestApplier, IHostUpdateRestoreExecutor, IHostUpdateDigestVerifier
    {
        public List<string> Calls { get; } = [];

        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? platformsByService = null)
        {
            Calls.Add("apply");
            return Task.CompletedTask;
        }

        public Task RestoreAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken)
        {
            Calls.Add("restore");
            return Task.CompletedTask;
        }

        public Task VerifyDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken)
        {
            Calls.Add("verify");
            return Task.CompletedTask;
        }
    }

    private sealed class StaticReconciliationGate : IHostUpdatePhysicalReconciliationGate
    {
        public bool Recorded { get; set; }

        public Task<bool> IsRecordedAsync(string releaseId, string requestId, CancellationToken cancellationToken) => Task.FromResult(Recorded);
    }

    private sealed class MemoryOutcomeStore : IHostUpdateRecoveryOutcomeStore
    {
        public List<HostUpdateRecoveryOutcomeRecord> Writes { get; } = [];

        public Task<HostUpdateRecoveryOutcomeRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken) =>
            Task.FromResult(Writes.LastOrDefault(w => string.Equals(w.ReleaseId, releaseId, StringComparison.Ordinal)));

        public Task WriteAsync(HostUpdateRecoveryOutcomeRecord record, CancellationToken cancellationToken)
        {
            Writes.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticInstalledStateStore(InstalledHostState state) : IInstalledHostStateStore
    {
        public Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<InstalledHostState?>(state);

        public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoBackupLocator : IHostUpdateBackupManifestLocator
    {
        public Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken) =>
            Task.FromResult<(HostUpdateBackupManifest, string)?>(null);
    }

    private sealed class InlinePolicyRepository(HostUpdateAutomationPolicy policy) : IHostUpdateAutomationPolicyRepository
    {
        public HostUpdatePolicyReadResult Read() => new(true, policy, null);

        public Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy replacement, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new HostUpdatePolicyReadResult(true, replacement, null));
    }

    private sealed class NoopLock : IHostUpdateExecutionLock
    {
        public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => new Lease();

        private sealed class Lease : IHostUpdateExecutionLease
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class NoopRecoveryLeaseProvider : IHostUpdateRecoveryLeaseProvider
    {
        public IHostUpdateRecoveryLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => new Lease();

        private sealed class Lease : IHostUpdateRecoveryLease
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class MemoryJournal : IHostUpdateExecutionJournal
    {
        private readonly List<HostUpdateExecutionActivity> _entries = [];

        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => [.. _entries.Where(e => e.ReleaseId == releaseId)];

        public IReadOnlyList<string> ListReleaseIds() => [.. _entries.Select(e => e.ReleaseId).Distinct(StringComparer.Ordinal)];

        public void Append(HostUpdateExecutionActivity activity) => _entries.Add(activity);
    }
}
