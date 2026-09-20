#pragma warning disable VSTHRD003
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutorTests
{
    private static HostUpdateAutomationPolicy Policy() => new(Enabled: true, Revision: 1);
    private static HostUpdateExecutionRequest Request() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, [
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

    [Fact] public void Request_requires_exact_unique_six_targets() { var request = Request() with { Targets = Request().Targets.Take(5).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_invalid", error); }
    [Fact] public void Request_rejects_duplicate_service_ids() { var request = Request() with { Targets = Request().Targets.Select((t, i) => i == 5 ? t with { ServiceId = "svc-1" } : t).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_set_invalid", error); }
    [Fact] public void Request_rejects_noncanonical_platform() { var request = Request() with { HostPlatform = "linux-armv8" }; Assert.False(request.IsValid(out var error)); Assert.Equal("release_binding_invalid", error); }
    [Fact] public void Journal_reconstructs_and_rejects_truncation() { string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".journal"); try { var journal = new FileHostUpdateExecutionJournal(path); journal.Append(new("a", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow)); Assert.Single(journal.Read("r")); File.WriteAllText(path, File.ReadAllText(path)[..^3]); Assert.Throws<InvalidDataException>(() => journal.Read("r")); } finally { if (File.Exists(path)) { File.Delete(path); } } }
    [Fact] public async Task Executor_persists_transition_order_and_completion_async() { var steps = new FakeSteps(); var journal = new MemoryJournal(); var executor = new HostUpdateExecutor(steps, journal, new NoopLock(), automationPolicyRepository: new InlinePolicyRepository(Policy())); var result = await executor.ExecuteAsync(Request()); Assert.True(result.Succeeded); Assert.Equal(new[] { "preflight", "drain", "fence", "backup", "migration", "apply", "verify" }, steps.Calls); }
    [Fact]
    public async Task Executor_rejects_policy_fingerprint_drift_before_execution()
    {
        var steps = new FakeSteps();
        HostUpdateExecutionResult result = await new HostUpdateExecutor(
            steps,
            new MemoryJournal(),
            new NoopLock(),
            automationPolicyRepository: new InlinePolicyRepository(Policy() with { PollIntervalSeconds = 1200 }))
            .ExecuteAsync(Request());

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
        Assert.Equal("policy_drifted", result.FailureCode);
        Assert.Empty(steps.Calls);
    }
    [Fact]
    public async Task Executor_defers_safe_checkpoint_cancellation_until_after_unsafe_apply()
    {
        var steps = new CancellationObservingSteps();
        var executor = new HostUpdateExecutor(steps, new MemoryJournal(), new NoopLock(), automationPolicyRepository: new InlinePolicyRepository(Policy()));
        using CancellationTokenSource cancellation = new();

        Task<HostUpdateExecutionResult> execution = executor.ExecuteAsync(Request(), cancellation.Token);
        await steps.ApplyStarted.Task;
        cancellation.Cancel();
        steps.ReleaseApply.TrySetResult();

        HostUpdateExecutionResult result = await execution;

        Assert.Equal(HostUpdateExecutionState.Applying, result.State);
        Assert.Equal("canceled", result.FailureCode);
        Assert.DoesNotContain("verify", steps.Calls);
    }

    [Fact]
    public async Task Executor_restart_in_recovery_remains_recovery_required()
    {
        var journal = new MemoryJournal();
        var failing = new FailingSteps();
        HostUpdateExecutionResult first = await new HostUpdateExecutor(failing, journal, new NoopLock(), automationPolicyRepository: new InlinePolicyRepository(Policy())).ExecuteAsync(Request());
        HostUpdateExecutionResult restarted = await new HostUpdateExecutor(new FakeSteps(), journal, new NoopLock(), automationPolicyRepository: new InlinePolicyRepository(Policy())).ExecuteAsync(Request());

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, first.State);
        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, restarted.State);
        Assert.Equal("recovery_required", restarted.FailureCode);
    }

    [Fact]
    public async Task Reserve_reliance_ExecutionLockAndJournal_survives_restart_without_reexecution()
    {
        string root = Directory.CreateTempSubdirectory("pf-host-update-reserve-").FullName;
        try
        {
            string replayPath = Path.Combine(root, "host-update-replay.json");
            await File.WriteAllTextAsync(replayPath, HostUpdateReplayPersistenceCodec.Serialize(HostUpdateReplayPersistenceCodec.Empty()));
            VerifiedHostUpdateCandidate candidate = Candidate();
            using (var replayBeforeCrash = new FileHostUpdateReplayStore(root, new InMemoryReplayAnchor()))
            {
                HostUpdateReplayDecision reservation = await replayBeforeCrash.DecideAsync(candidate, HostUpdateReplayIntent.Reserve, default);
                Assert.Equal(HostUpdateReplayDisposition.Accepted, reservation.Disposition);
            }

            string journalPath = Path.Combine(root, "journal.ndjson");
            string lockPath = Path.Combine(root, "execution.lock");
            HostUpdateExecutionResult first = await new HostUpdateExecutor(
                new FailingSteps(),
                new FileHostUpdateExecutionJournal(journalPath),
                new FileHostUpdateExecutionLock(lockPath),
                automationPolicyRepository: new InlinePolicyRepository(Policy()))
                .ExecuteAsync(Request());

            HostUpdateReplayDecision restartedReservation;
            using (var replayAfterCrash = new FileHostUpdateReplayStore(root, new InMemoryReplayAnchor()))
            {
                restartedReservation = await replayAfterCrash.DecideAsync(candidate, HostUpdateReplayIntent.Reserve, default);
            }
            var restartedSteps = new FakeSteps();
            HostUpdateExecutionResult restarted = await new HostUpdateExecutor(
                restartedSteps,
                new FileHostUpdateExecutionJournal(journalPath),
                new FileHostUpdateExecutionLock(lockPath),
                automationPolicyRepository: new InlinePolicyRepository(Policy()))
                .ExecuteAsync(Request());

            Assert.Equal(HostUpdateExecutionState.RecoveryRequired, first.State);
            Assert.Equal(HostUpdateReplayDisposition.Accepted, restartedReservation.Disposition);
            Assert.Equal(HostUpdateExecutionState.RecoveryRequired, restarted.State);
            Assert.Equal("recovery_required", restarted.FailureCode);
            Assert.Empty(restartedSteps.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static VerifiedHostUpdateCandidate Candidate() => new(
        "rel-1",
        new string('b', 40),
        1,
        "sha256:" + new string('a', 64),
        "Stable",
        CryptographicallyVerified: true,
        CompatibilityReady: true,
        InstallationAvailable: true,
        SafetyPassed: true,
        MaintenanceWindowOpen: true,
        IsNewer: true,
        new HostUpdatePlatformDigests(
            "sha256:" + new string('1', 64),
            "sha256:" + new string('2', 64),
            "sha256:" + new string('3', 64),
            "sha256:" + new string('4', 64),
            "sha256:" + new string('5', 64),
            "sha256:" + new string('6', 64)),
        TrustRoot: "root-1");

    private sealed class InMemoryReplayAnchor : IHostUpdateReplayAnchor
    {
        public Task<long> ReadEpochAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task<string> ReadStateHashAsync(CancellationToken ct) =>
            Task.FromResult(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(HostUpdateReplayPersistenceCodec.Serialize(HostUpdateReplayPersistenceCodec.Empty())))));
        public Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct) => Task.CompletedTask;
    }


    [Fact]
    public void File_journal_preserves_complete_history_and_discards_interrupted_stage()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".journal");
        try
        {
            FileHostUpdateExecutionJournal journal = new(path);
            journal.Append(new("1", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow));
            journal.Append(new("2", "r", HostUpdateExecutionState.Preflight, "preflight:before", DateTimeOffset.UtcNow));
            journal.Append(new("3", "r", HostUpdateExecutionState.Preflight, "preflight:after", DateTimeOffset.UtcNow));
            Assert.Equal(3, journal.Read("r").Count);

            File.WriteAllText(path + ".staged", "{truncated");
            Assert.Equal(3, new FileHostUpdateExecutionJournal(path).Read("r").Count);
            Assert.False(File.Exists(path + ".staged"));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(path + ".staged"))
            {
                File.Delete(path + ".staged");
            }
        }
    }

    [Theory]
    [InlineData(HostUpdateExecutionState.Migrating, "migration")]
    [InlineData(HostUpdateExecutionState.Applying, "apply")]
    public async Task Real_file_restart_fails_closed_when_unmatched_unsafe_phase_cannot_be_reconciled(HostUpdateExecutionState state, string phase)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".journal");
        try
        {
            HostUpdateExecutionRequest request = Request();
            FileHostUpdateExecutionJournal journal = new(path);
            journal.Append(new("before", request.ReleaseId, state, phase + ":before", DateTimeOffset.UtcNow)
            {
                RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            });
            FakeSteps steps = new();

            HostUpdateExecutionResult result = await new HostUpdateExecutor(steps, new FileHostUpdateExecutionJournal(path), new NoopLock(), automationPolicyRepository: new InlinePolicyRepository(Policy())).ExecuteAsync(request);

            Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
            Assert.Equal("uncertain_side_effect:" + phase + ":reconciler_unavailable", result.FailureCode);
            Assert.DoesNotContain(phase, steps.Calls);
            HostUpdateExecutionActivity durable = Assert.Single(new FileHostUpdateExecutionJournal(path).Read(request.ReleaseId), activity => activity.State == HostUpdateExecutionState.RecoveryRequired);
            Assert.Equal("failure:uncertain_side_effect:" + phase + ":reconciler_unavailable", durable.Phase);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
    private sealed class InlinePolicyRepository(HostUpdateAutomationPolicy policy) : IHostUpdateAutomationPolicyRepository
    {
        public HostUpdatePolicyReadResult Read() => new(true, policy, null);
        public Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy replacement, long expectedRevision, CancellationToken ct) => Task.FromResult(new HostUpdatePolicyReadResult(true, replacement, null));
    }

    private sealed class CancellationObservingSteps : IHostUpdateExecutionSteps
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreflightAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("preflight");
        public Task DrainAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("drain");
        public Task FenceAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("fence");
        public Task BackupAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("backup");
        public Task MigrateAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("migration");
        public async Task ApplyAsync(HostUpdateExecutionRequest r, CancellationToken c) { Calls.Add("apply"); ApplyStarted.SetResult(); await ReleaseApply.Task; }
        public Task VerifyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("verify");
        private Task AddAsync(string value) { Calls.Add(value); return Task.CompletedTask; }
    }

    private sealed class FailingSteps : FakeSteps
    {
        public override Task ApplyAsync(HostUpdateExecutionRequest r, CancellationToken c) => throw new InvalidOperationException("apply_failed");
    }

    private sealed class NoopLock : IHostUpdateExecutionLock { public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => new Lease(); private sealed class Lease : IHostUpdateExecutionLease { public void Dispose() { } } }
    private sealed class MemoryJournal : IHostUpdateExecutionJournal { private readonly List<HostUpdateExecutionActivity> entries = []; public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => entries.Where(e => e.ReleaseId == releaseId).ToArray(); public IReadOnlyList<string> ListReleaseIds() => entries.Select(e => e.ReleaseId).Distinct(StringComparer.Ordinal).ToArray(); public void Append(HostUpdateExecutionActivity activity) => entries.Add(activity); }
    private class FakeSteps : IHostUpdateExecutionSteps { public List<string> Calls { get; } = []; public Task PreflightAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("preflight"); public Task DrainAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("drain"); public Task FenceAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("fence"); public Task BackupAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("backup"); public Task MigrateAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("migration"); public virtual Task ApplyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("apply"); public Task VerifyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("verify"); private Task AddAsync(string value) { Calls.Add(value); return Task.CompletedTask; } }
}
