using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutorTests
{
    private static HostUpdateExecutionRequest Request() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, Enumerable.Range(1, 6).Select(i => new HostUpdateExecutionTarget("svc-" + i, "linux-amd64", "sha256:" + new string("abcdef"[i - 1], 64))).ToArray());

    [Fact] public void Request_requires_exact_unique_six_targets() { var request = Request() with { Targets = Request().Targets.Take(5).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("release_identity_invalid", error); }
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
        Assert.Equal(new[] { "preflight", "drain", "fence", "backup", "migration", "apply", "verify" }, steps.Calls);
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
        Assert.Equal("uncertain_side_effect:migration", result.FailureCode);
        Assert.Empty(steps.Calls);
        Assert.Equal("failure:uncertain_side_effect:migration", journal.Read(request.ReleaseId)[^1].Phase);
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
        Assert.Equal("uncertain_side_effect:apply", result.FailureCode);
        Assert.Empty(steps.Calls);
        Assert.Equal("failure:uncertain_side_effect:apply", journal.Read(request.ReleaseId)[^1].Phase);
    }

    private static HostUpdateExecutionActivity Activity(HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase) =>
        new(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow, HostUpdateRequestFingerprint.Compute(request));

    private sealed class NoopLock : IHostUpdateExecutionLock { public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => new Lease(); private sealed class Lease : IHostUpdateExecutionLease { public void Dispose() { } } }
    private sealed class MemoryJournal(params HostUpdateExecutionActivity[] seed) : IHostUpdateExecutionJournal { private readonly List<HostUpdateExecutionActivity> entries = [.. seed]; public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => entries.Where(e => e.ReleaseId == releaseId).ToArray(); public void Append(HostUpdateExecutionActivity activity) => entries.Add(activity); public IReadOnlyList<string> ListReleaseIds() => entries.Select(e => e.ReleaseId).Distinct().ToArray(); }
    private sealed class FakeSteps : IHostUpdateExecutionSteps { public List<string> Calls { get; } = []; public List<CancellationToken> StepTokens { get; } = []; public Task PreflightAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("preflight", c); public Task DrainAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("drain", c); public Task FenceAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("fence", c); public Task BackupAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("backup", c); public Task MigrateAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("migration", c); public Task ApplyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("apply", c); public Task VerifyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("verify", c); private Task AddAsync(string value, CancellationToken cancellationToken) { Calls.Add(value); StepTokens.Add(cancellationToken); return Task.CompletedTask; } }
}
