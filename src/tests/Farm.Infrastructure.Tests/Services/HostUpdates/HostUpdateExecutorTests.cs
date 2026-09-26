#pragma warning disable VSTHRD003
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutorTests
{
    private static HostUpdateAutomationPolicy Policy() => new(Enabled: true, Revision: 1);
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

    [Fact] public void Request_requires_exact_unique_six_targets() { var request = Request() with { Targets = Request().Targets.Take(5).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_invalid", error); }
    [Fact] public void Request_rejects_duplicate_service_ids() { var request = Request() with { Targets = Request().Targets.Select((t, i) => i == 5 ? t with { ServiceId = "svc-1" } : t).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_set_invalid", error); }
    [Fact] public void Request_rejects_noncanonical_platform() { var request = Request() with { HostPlatform = "linux-armv8" }; Assert.False(request.IsValid(out var error)); Assert.Equal("release_binding_invalid", error); }
    [Fact]
    public void Request_binding_preserves_legacy_registry_hash_and_protects_preloaded_mode()
    {
        HostUpdateExecutionRequest registry = Request();
        string legacy = HostUpdateCanonical.Hash(new
        {
            registry.TrustRoot,
            registry.PolicyRevision,
            registry.PolicyFingerprint,
            registry.ReleaseId,
            registry.Channel,
            registry.RequestId,
            registry.AuthenticatedSequence,
            registry.ManifestDigest,
            registry.SourceCommit,
            registry.HostPlatform,
            registry.AuthorizationKind,
            Targets = registry.Targets.OrderBy(target => target.ServiceId, StringComparer.Ordinal),
        });

        string registryHash = HostUpdateRequestBinding.Compute(registry);
        string preloadedHash = HostUpdateRequestBinding.Compute(registry with { ImageSourceMode = HostUpdateImageSourceMode.PreloadedLocal });

        Assert.Equal(legacy, registryHash);
        Assert.NotEqual(registryHash, preloadedHash);
    }

    [Fact] public void Journal_reconstructs_and_rejects_truncation() { string path = Path.Combine(HostStateTestPaths.TempRoot, Guid.NewGuid() + ".journal"); try { var journal = new FileHostUpdateExecutionJournal(path); journal.Append(new("a", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow)); Assert.Single(journal.Read("r")); File.WriteAllText(path, File.ReadAllText(path)[..^3]); Assert.Throws<InvalidDataException>(() => journal.Read("r")); } finally { if (File.Exists(path)) { File.Delete(path); } } }
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
        string root = HostStateTestPaths.CreateTempSubdirectory("pf-host-update-reserve-").FullName;
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

    [Fact]
    public void TryAcquireExisting_takes_the_lock_without_creating_or_rewriting_the_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "pf-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string lockPath = Path.Combine(root, FileHostUpdateExecutionLock.FileName);
            Assert.Null(FileHostUpdateExecutionLock.TryAcquireExisting(lockPath));
            Assert.False(File.Exists(lockPath));

            File.WriteAllText(lockPath, "pid=1");
            using (IHostUpdateExecutionLease? probe = FileHostUpdateExecutionLock.TryAcquireExisting(lockPath))
            {
                Assert.NotNull(probe);
                Assert.Throws<TimeoutException>(() => new FileHostUpdateExecutionLock(lockPath).Acquire(TimeSpan.Zero, CancellationToken.None));
            }

            Assert.Equal("pid=1", File.ReadAllText(lockPath));
            using (new FileHostUpdateExecutionLock(lockPath).Acquire(TimeSpan.Zero, CancellationToken.None))
            {
                Assert.Throws<TimeoutException>(() => FileHostUpdateExecutionLock.TryAcquireExisting(lockPath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static VerifiedHostUpdateCandidate Candidate() => new(
        "stable:1.2.3",
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
        string path = Path.Combine(HostStateTestPaths.TempRoot, Guid.NewGuid() + ".journal");
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
        string path = Path.Combine(HostStateTestPaths.TempRoot, Guid.NewGuid() + ".journal");
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

    // --- Issue #3047: authorization-time drift baseline ----------------------------------------

    [Fact]
    public async Task Executor_journals_the_authorization_baseline_on_the_accepted_activity_only()
    {
        var baseline = new HostUpdateAuthorizationBaseline(1, "none", "sha256:config", HostUpdateTrustRoot.Fingerprint);
        var journal = new MemoryJournal();
        var provider = new StubBaselineProvider(() => baseline);

        HostUpdateExecutionResult result = await new HostUpdateExecutor(
            new FakeSteps(), journal, new NoopLock(), new InlinePolicyRepository(Policy()), baselineProvider: provider).ExecuteAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(1, provider.Calls);
        HostUpdateExecutionActivity accepted = Assert.Single(result.Activities, a => a.Phase == "accepted");
        Assert.Equal(baseline, accepted.AuthorizationBaseline);
        Assert.All(result.Activities.Where(a => a.Phase != "accepted"), a => Assert.Null(a.AuthorizationBaseline));
    }

    [Fact]
    public async Task Executor_refuses_to_start_when_the_baseline_cannot_be_captured()
    {
        var steps = new FakeSteps();
        var journal = new MemoryJournal();

        HostUpdateExecutionResult result = await new HostUpdateExecutor(
            steps, journal, new NoopLock(), new InlinePolicyRepository(Policy()),
            baselineProvider: new StubBaselineProvider(() => throw new InvalidDataException("installed_state_corrupt"))).ExecuteAsync(Request());

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
        Assert.Equal("authorization_baseline_unavailable", result.FailureCode);
        Assert.Empty(steps.Calls);
        Assert.Empty(journal.Read(Request().ReleaseId));
    }

    [Fact]
    public async Task Executor_does_not_recapture_the_baseline_once_execution_started()
    {
        var journal = new MemoryJournal();
        await new HostUpdateExecutor(new FailingSteps(), journal, new NoopLock(), new InlinePolicyRepository(Policy())).ExecuteAsync(Request());
        var provider = new StubBaselineProvider(() => throw new InvalidOperationException("must_not_be_called"));

        HostUpdateExecutionResult restarted = await new HostUpdateExecutor(
            new FakeSteps(), journal, new NoopLock(), new InlinePolicyRepository(Policy()), baselineProvider: provider).ExecuteAsync(Request());

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, restarted.State);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Executor_does_not_rebase_a_legacy_accepted_entry_onto_current_host_state()
    {
        var journal = new MemoryJournal();
        journal.Append(new("legacy", Request().ReleaseId, HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow.AddHours(-1)));
        var provider = new StubBaselineProvider(() => new HostUpdateAuthorizationBaseline(1, "sha256:rebased", "sha256:rebased", HostUpdateTrustRoot.Fingerprint));

        HostUpdateExecutionResult result = await new HostUpdateExecutor(
            new FailingSteps(), journal, new NoopLock(), new InlinePolicyRepository(Policy()), baselineProvider: provider).ExecuteAsync(Request());

        Assert.Equal(0, provider.Calls);
        Assert.All(result.Activities, a => Assert.Null(a.AuthorizationBaseline));
        Assert.All(journal.Read(Request().ReleaseId), a => Assert.Null(a.AuthorizationBaseline));
    }

    [Fact]
    public void Journal_trusts_only_the_hashed_payload_not_the_outer_activity_copy()
    {
        string path = Path.Combine(HostStateTestPaths.TempRoot, Guid.NewGuid() + ".journal");
        try
        {
            var baseline = new HostUpdateAuthorizationBaseline(1, "sha256:" + new string('1', 64), "sha256:config", HostUpdateTrustRoot.Fingerprint);
            new FileHostUpdateExecutionJournal(path).Append(
                new("a", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow) { AuthorizationBaseline = baseline });

            System.Text.Json.Nodes.JsonNode line = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path).TrimEnd('\n'))!;
            line["Activity"]!["AuthorizationBaseline"]!["ConfigurationFingerprint"] = "sha256:forged";
            line["Activity"]!["ReleaseId"] = "r";
            File.WriteAllText(path, line.ToJsonString() + "\n");

            HostUpdateExecutionActivity read = Assert.Single(new FileHostUpdateExecutionJournal(path).Read("r"));

            Assert.Equal(baseline, read.AuthorizationBaseline);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Journaled_baseline_round_trips_and_is_absent_from_legacy_entries()
    {
        string path = Path.Combine(HostStateTestPaths.TempRoot, Guid.NewGuid() + ".journal");
        try
        {
            var baseline = new HostUpdateAuthorizationBaseline(1, "sha256:" + new string('1', 64), "sha256:config", HostUpdateTrustRoot.Fingerprint);
            var journal = new FileHostUpdateExecutionJournal(path);
            journal.Append(new("a", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow) { AuthorizationBaseline = baseline });
            journal.Append(new("b", "r", HostUpdateExecutionState.Preflight, "preflight:before", DateTimeOffset.UtcNow));

            IReadOnlyList<HostUpdateExecutionActivity> read = new FileHostUpdateExecutionJournal(path).Read("r");

            Assert.Equal(baseline, read[0].AuthorizationBaseline);
            Assert.Null(read[1].AuthorizationBaseline);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Baseline_provider_hashes_installed_state_independent_of_dictionary_order()
    {
        var options = new HostUpdateExecutionOptions();
        var database = new Farm.Infrastructure.Data.DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=/srv/farm.db" };
        InstalledHostState ordered = Installed(new() { ["api"] = "sha256:1", ["frontend"] = "sha256:2" });
        InstalledHostState reversed = Installed(new() { ["frontend"] = "sha256:2", ["api"] = "sha256:1" });

        HostUpdateAuthorizationBaseline first = await new HostUpdateAuthorizationBaselineProvider(new StaticStateStore(ordered), options, database, new StaticBindingReader("sha256:m")).CaptureAsync(Request(), CancellationToken.None);
        HostUpdateAuthorizationBaseline second = await new HostUpdateAuthorizationBaselineProvider(new StaticStateStore(reversed), options, database, new StaticBindingReader("sha256:m")).CaptureAsync(Request(), CancellationToken.None);
        HostUpdateAuthorizationBaseline absent = await new HostUpdateAuthorizationBaselineProvider(new StaticStateStore(null), options, database, new StaticBindingReader("sha256:m")).CaptureAsync(Request(), CancellationToken.None);

        Assert.Equal(HostUpdateAuthorizationBaseline.CurrentSchemaVersion, first.SchemaVersion);
        Assert.Equal(first, second);
        Assert.Matches("^sha256:[0-9a-f]{64}$", first.InstalledStateHash);
        Assert.Equal(HostUpdateBaselineHashes.NoInstalledState, absent.InstalledStateHash);
        Assert.Equal(HostUpdateTrustRoot.Fingerprint, first.TrustRootFingerprint);
        Assert.Equal("sha256:m", first.ManifestBinding);
        Assert.NotEqual(first.InstalledStateHash, HostUpdateBaselineHashes.InstalledState(ordered with { ReleaseId = "stable:1.2.1" }));
    }

    [Fact]
    public void Trust_root_pins_the_release_workflow_identities()
    {
        Assert.Equal("https://token.actions.githubusercontent.com", HostUpdateTrustRoot.CosignIssuer);
        Assert.EndsWith("consolidated-release.yml@refs/heads/main", HostUpdateTrustRoot.CertificateIdentity("stable"), StringComparison.Ordinal);
        Assert.EndsWith("consolidated-release.yml@refs/heads/development", HostUpdateTrustRoot.CertificateIdentity("insider"), StringComparison.Ordinal);
        Assert.True(HostUpdateTrustRoot.IsPinned("default"));
        Assert.False(HostUpdateTrustRoot.IsPinned("root-1"));
        Assert.Matches("^sha256:[0-9a-f]{64}$", HostUpdateTrustRoot.Fingerprint);
    }

    private static InstalledHostState Installed(Dictionary<string, string> digests) =>
        new("stable:1.2.2", "sha256:" + new string('9', 64), digests, "api+frontend", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class StubBaselineProvider(Func<HostUpdateAuthorizationBaseline> capture) : IHostUpdateAuthorizationBaselineProvider
    {
        public int Calls { get; private set; }

        public Task<HostUpdateAuthorizationBaseline> CaptureAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(capture());
        }
    }

    private sealed class StaticBindingReader(string binding) : IHostUpdateManifestBindingReader
    {
        public Task<string> ReadAsync(string releaseId, CancellationToken cancellationToken) => Task.FromResult(binding);
    }

    private sealed class StaticStateStore(InstalledHostState? state) : IInstalledHostStateStore
    {
        public Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(state);

        public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken) => Task.CompletedTask;
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
