using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutorTests
{
    private static HostUpdateExecutionRequest Request() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, Enumerable.Range(1, 6).Select(i => new HostUpdateExecutionTarget("svc-" + i, "linux-amd64", "sha256:" + new string("abcdef"[i - 1], 64))).ToArray());

    [Fact] public void Request_requires_at_least_one_target() { var request = Request() with { Targets = [] }; Assert.False(request.IsValid(out var error)); Assert.Equal("release_identity_invalid", error); }
    [Fact] public void Request_rejects_duplicate_service_ids() { var request = Request() with { Targets = Request().Targets.Select((t, i) => i == 5 ? t with { ServiceId = "svc-1" } : t).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_set_invalid", error); }
    [Fact] public void Journal_reconstructs_and_rejects_truncation() { string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".journal"); try { var journal = new FileHostUpdateExecutionJournal(path); journal.Append(Activity(Request(), HostUpdateExecutionState.Accepted, "accepted")); Assert.Single(journal.Read("rel-1")); File.WriteAllText(path, File.ReadAllText(path)[..^3]); Assert.Throws<InvalidDataException>(() => journal.Read("rel-1")); } finally { if (File.Exists(path)) { File.Delete(path); } } }

    // Bishop/Hicks review (issue #2663): FileHostUpdateExecutionJournal.Append used an atomic
    // temp-file-then-move pattern that computed the correct previous-hash chain but then wrote
    // ONLY the newest record to the temp file before moving it over the journal path -- silently
    // discarding every prior record on every append. A single-record test could never catch this
    // (there was nothing to discard yet). This proves the full chain survives 3+ appends, and
    // survives being read back from brand-new journal instances (simulating a process restart
    // between each append), not just from the same in-memory instance that wrote them.
    [Fact]
    public void Journal_preserves_full_chain_across_multiple_appends_and_reconstructed_instances()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".journal");
        try
        {
            HostUpdateExecutionRequest request = Request();
            new FileHostUpdateExecutionJournal(path).Append(Activity(request, HostUpdateExecutionState.Accepted, "accepted"));
            new FileHostUpdateExecutionJournal(path).Append(Activity(request, HostUpdateExecutionState.Preflight, "preflight_ok"));
            new FileHostUpdateExecutionJournal(path).Append(Activity(request, HostUpdateExecutionState.Draining, "drain_ok"));

            IReadOnlyList<HostUpdateExecutionActivity> entries = new FileHostUpdateExecutionJournal(path).Read("rel-1");

            Assert.Equal(3, entries.Count);
            Assert.Equal(
                new[] { HostUpdateExecutionState.Accepted, HostUpdateExecutionState.Preflight, HostUpdateExecutionState.Draining },
                entries.Select(e => e.State));
            Assert.Equal(3, File.ReadAllLines(path).Length);
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
    public async Task Executor_persists_transition_order_and_completion_async()
    {
        var steps = new FakeSteps();
        var journal = new MemoryJournal();
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock());
        using var cts = new CancellationTokenSource();
        var result = await executor.ExecuteAsync(Request(), cts.Token);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "preflight", "drain", "fence", "backup", "migration", "apply", "verify", "persist-installed", "release-fence" }, steps.Calls);
        result.Activities.Where(a => a.State == HostUpdateExecutionState.Completed).Select(a => a.Phase).Should().ContainInOrder("completed", "installed-state:before", "installed-state:after", "fence-release:before", "fence-release:after");
        Assert.All(steps.StepTokens, token => Assert.True(token.CanBeCanceled));
        Assert.All(result.Activities, activity => Assert.False(string.IsNullOrWhiteSpace(activity.RequestFingerprint)));
    }

    [Fact]
    public async Task Executor_refuses_same_release_resume_with_changed_immutable_request_fingerprint()
    {
        HostUpdateExecutionRequest original = Request();
        HostUpdateExecutionRequest tampered = original with { ManifestDigest = "sha256:" + new string('c', 64) };
        var steps = new FakeSteps();
        var journal = new MemoryJournal(Activity(original, HostUpdateExecutionState.Accepted, "accepted"));
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock());

        HostUpdateExecutionResult result = await executor.ExecuteAsync(tampered);

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
        Assert.Equal("request_fingerprint_mismatch", result.FailureCode);
        Assert.Empty(steps.Calls);
        Assert.Equal("failure:request_fingerprint_mismatch", journal.Read(original.ReleaseId)[^1].Phase);
    }

    [Fact]
    public async Task Executor_refuses_unfingerprinted_prior_journal_instead_of_guessing_resume_identity()
    {
        HostUpdateExecutionRequest request = Request();
        var steps = new FakeSteps();
        var journal = new MemoryJournal(new HostUpdateExecutionActivity("legacy", request.ReleaseId, HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow));
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock());

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request);

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
        Assert.Equal("request_fingerprint_missing", result.FailureCode);
        Assert.Empty(steps.Calls);
    }

    [Fact]
    public async Task Executor_does_not_replay_migration_after_restart_when_before_receipt_exists_without_after()
    {
        HostUpdateExecutionRequest request = Request();
        var steps = new FakeSteps();
        var journal = new MemoryJournal(
            Activity(request, HostUpdateExecutionState.Accepted, "accepted"),
            Activity(request, HostUpdateExecutionState.Preflight, "preflight:after"),
            Activity(request, HostUpdateExecutionState.Draining, "drain:after"),
            Activity(request, HostUpdateExecutionState.Fenced, "fence:after"),
            Activity(request, HostUpdateExecutionState.BackedUp, "backup:after"),
            Activity(request, HostUpdateExecutionState.Migrating, "migration:before"));
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock());

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request);

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
        Assert.Equal("uncertain_side_effect:migration:reconciler_unavailable", result.FailureCode);
        Assert.Empty(steps.Calls);
        Assert.Equal("failure:uncertain_side_effect:migration:reconciler_unavailable", journal.Read(request.ReleaseId)[^1].Phase);
    }

    [Fact]
    public async Task Executor_does_not_replay_apply_after_restart_when_before_receipt_exists_without_after()
    {
        HostUpdateExecutionRequest request = Request();
        var steps = new FakeSteps();
        var journal = new MemoryJournal(
            Activity(request, HostUpdateExecutionState.Accepted, "accepted"),
            Activity(request, HostUpdateExecutionState.Preflight, "preflight:after"),
            Activity(request, HostUpdateExecutionState.Draining, "drain:after"),
            Activity(request, HostUpdateExecutionState.Fenced, "fence:after"),
            Activity(request, HostUpdateExecutionState.BackedUp, "backup:after"),
            Activity(request, HostUpdateExecutionState.Migrating, "migration:after"),
            Activity(request, HostUpdateExecutionState.Applying, "apply:before"));
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock());

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request);

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
        Assert.Equal("uncertain_side_effect:apply:reconciler_unavailable", result.FailureCode);
        Assert.Empty(steps.Calls);
        Assert.Equal("failure:uncertain_side_effect:apply:reconciler_unavailable", journal.Read(request.ReleaseId)[^1].Phase);
    }


    [Fact]
    public async Task Executor_reconciles_completed_migration_after_restart_without_replaying_external_side_effect()
    {
        HostUpdateExecutionRequest request = Request();
        var steps = new FakeSteps();
        var journal = new MemoryJournal(
            Activity(request, HostUpdateExecutionState.Accepted, "accepted"),
            Activity(request, HostUpdateExecutionState.Preflight, "preflight:after"),
            Activity(request, HostUpdateExecutionState.Draining, "drain:after"),
            Activity(request, HostUpdateExecutionState.Fenced, "fence:after"),
            Activity(request, HostUpdateExecutionState.BackedUp, "backup:after"),
            Activity(request, HostUpdateExecutionState.Migrating, "migration:before"));
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock(), new FakeSideEffectReconciler("migration"));

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "apply", "verify", "persist-installed", "release-fence" }, steps.Calls);
        Assert.Contains(journal.Read(request.ReleaseId), a => a.State == HostUpdateExecutionState.Migrating && a.Phase == "migration:after");
    }

    [Fact]
    public async Task Executor_reconciles_completed_apply_after_restart_without_replaying_compose()
    {
        HostUpdateExecutionRequest request = Request();
        var steps = new FakeSteps();
        var journal = new MemoryJournal(
            Activity(request, HostUpdateExecutionState.Accepted, "accepted"),
            Activity(request, HostUpdateExecutionState.Preflight, "preflight:after"),
            Activity(request, HostUpdateExecutionState.Draining, "drain:after"),
            Activity(request, HostUpdateExecutionState.Fenced, "fence:after"),
            Activity(request, HostUpdateExecutionState.BackedUp, "backup:after"),
            Activity(request, HostUpdateExecutionState.Migrating, "migration:after"),
            Activity(request, HostUpdateExecutionState.Applying, "apply:before"));
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock(), new FakeSideEffectReconciler("apply"));

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "verify", "persist-installed", "release-fence" }, steps.Calls);
        Assert.DoesNotContain("apply", steps.Calls);
        Assert.Contains(journal.Read(request.ReleaseId), a => a.State == HostUpdateExecutionState.Applying && a.Phase == "apply:after");
    }

    [Fact]
    public async Task Executor_persists_recovery_required_when_side_effect_reconciliation_is_unproven()
    {
        HostUpdateExecutionRequest request = Request();
        var steps = new FakeSteps();
        var journal = new MemoryJournal(
            Activity(request, HostUpdateExecutionState.Accepted, "accepted"),
            Activity(request, HostUpdateExecutionState.Preflight, "preflight:after"),
            Activity(request, HostUpdateExecutionState.Draining, "drain:after"),
            Activity(request, HostUpdateExecutionState.Fenced, "fence:after"),
            Activity(request, HostUpdateExecutionState.BackedUp, "backup:after"),
            Activity(request, HostUpdateExecutionState.Migrating, "migration:after"),
            Activity(request, HostUpdateExecutionState.Applying, "apply:before"));
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock(), new FakeSideEffectReconciler(reconciledPhase: null));

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request);

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
        Assert.Equal("uncertain_side_effect:apply:not_proven", result.FailureCode);
        Assert.Empty(steps.Calls);
        Assert.Equal("failure:uncertain_side_effect:apply:not_proven", journal.Read(request.ReleaseId)[^1].Phase);
    }

    [Fact]
    public async Task RequestResolver_LoadsOnlyCompletedStagedServerSideReceipt()
    {
        var identity = new CanonicalReleaseIdentity(
            "stable:1.0.0", "1.0.0", "stable", "v1.0.0", "main", new string('b', 40), new string('b', 40),
            "build-1", "stable:1.0.0", "1.0.0", "sha256:" + new string('a', 64));
        var prior = identity with { ReleaseId = "stable:0.9.0", Version = "0.9.0" };
        var receipt = new HostUpdateStagingReceipt(
            true,
            "staged",
            identity,
            identity.ManifestDigest,
            new Dictionary<string, string> { ["api/linux-amd64"] = "sha256:" + new string('c', 64), ["frontend/linux-amd64"] = "sha256:" + new string('d', 64) },
            prior,
            "sha256:" + new string('e', 64),
            "sha256:" + new string('f', 64));
        var journal = new MemoryFoundationJournal(new[] {
            new HostUpdateJournalEntry(
                1,
                DateTimeOffset.UtcNow,
                "op-1",
                "idem-1",
                HostUpdateLifecycle.Staged,
                "staged",
                new HostUpdateJournalSnapshot(
                    "installation-1",
                    "operator",
                    "nonce",
                    "reason",
                    "stable",
                    "stable",
                    "rev-1",
                    "plan-hash",
                    identity,
                    receipt,
                    "topology",
                    new HashSet<string> { "api" },
                    "linux-amd64",
                    receipt.ComponentPlatformDigests)),
        });
        var resolver = new HostUpdateExecutionRequestResolver(journal);

        HostUpdateExecutionRequest? request = await resolver.ResolveAsync(identity.ReleaseId, CancellationToken.None);

        request.Should().NotBeNull();
        request!.ReleaseId.Should().Be(identity.ReleaseId);
        request.ManifestDigest.Should().Be(identity.ManifestDigest);
        request.SourceCommit.Should().Be(identity.SourceCommit);
        request.Channel.Should().Be(HostUpdateExecutionChannel.Stable);
        request.Targets.Should().ContainSingle();
        request.Targets[0].ServiceId.Should().Be("api");
    }

    [Fact]
    public async Task RequestResolver_RejectsMissingCompletedStagingReceipt()
    {
        var resolver = new HostUpdateExecutionRequestResolver(new MemoryFoundationJournal([]));

        HostUpdateExecutionRequest? request = await resolver.ResolveAsync("stable:1.0.0", CancellationToken.None);

        request.Should().BeNull();
    }
    private static HostUpdateExecutionActivity Activity(HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase) =>
        new(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow, HostUpdateRequestFingerprint.Compute(request));

    private sealed class MemoryFoundationJournal(IReadOnlyList<HostUpdateJournalEntry> entries) : IHostUpdateJournal { public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) => throw new NotSupportedException(); public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => Task.FromResult(entries); }
    private sealed class NoopLock : IHostUpdateExecutionLock { public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => new Lease(); private sealed class Lease : IHostUpdateExecutionLease { public void Dispose() { } } }
    private sealed class MemoryJournal(params HostUpdateExecutionActivity[] seed) : IHostUpdateExecutionJournal { private readonly List<HostUpdateExecutionActivity> entries = [.. seed]; public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => entries.Where(e => e.ReleaseId == releaseId).ToArray(); public void Append(HostUpdateExecutionActivity activity) => entries.Add(activity); public IReadOnlyList<string> ListReleaseIds() => entries.Select(e => e.ReleaseId).Distinct().ToArray(); }

    [Fact]
    public async Task ExecuteAsync_MigrationRunnerUnavailable_FailsClosedBeforeOldAssemblyMigrationCanRun()
    {
        var steps = new FakeSteps { MigrationException = new HostUpdateTargetImageMigrationRunnerUnavailableException() };
        var journal = new MemoryJournal();
        var executor = new HostUpdateExecutor(steps, journal, new NoopLock());

        HostUpdateExecutionResult result = await executor.ExecuteAsync(Request());

        result.State.Should().Be(HostUpdateExecutionState.RecoveryRequired);
        result.FailureCode.Should().Be(nameof(HostUpdateTargetImageMigrationRunnerUnavailableException));
        steps.Calls.Should().Contain("migration");
        steps.Calls.Should().NotContain("apply");
        journal.Read(Request().ReleaseId).Should().Contain(a => a.State == HostUpdateExecutionState.RecoveryRequired);
    }

    private sealed class FakeSideEffectReconciler(string? reconciledPhase) : IHostUpdateSideEffectReconciler { public Task<HostUpdateSideEffectReconciliation> ReconcileAsync(string phase, HostUpdateExecutionRequest request, CancellationToken cancellationToken) => Task.FromResult(string.Equals(phase, reconciledPhase, StringComparison.Ordinal) ? HostUpdateSideEffectReconciliation.Complete("proven") : HostUpdateSideEffectReconciliation.Uncertain("not_proven")); }
    private sealed class FakeSteps : IHostUpdateExecutionSteps { public List<string> Calls { get; } = []; public List<CancellationToken> StepTokens { get; } = []; public Exception? MigrationException { get; set; } public Task PreflightAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("preflight", c); public Task DrainAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("drain", c); public Task FenceAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("fence", c); public Task BackupAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("backup", c); public async Task MigrateAsync(HostUpdateExecutionRequest r, CancellationToken c) { await AddAsync("migration", c); if (MigrationException is not null) { throw MigrationException; } } public Task ApplyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("apply", c); public Task VerifyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("verify", c); public Task PersistInstalledStateAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("persist-installed", c); public Task ReleaseFenceAsync(CancellationToken c) => AddAsync("release-fence", c); private Task AddAsync(string value, CancellationToken cancellationToken) { Calls.Add(value); StepTokens.Add(cancellationToken); return Task.CompletedTask; } }
}
