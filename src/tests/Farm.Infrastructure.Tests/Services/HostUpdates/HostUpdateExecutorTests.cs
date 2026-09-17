using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutorTests
{
    private static HostUpdateExecutionRequest Request() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, Enumerable.Range(1, 6).Select(i => new HostUpdateExecutionTarget("svc-" + i, "linux-amd64", "sha256:" + new string("abcdef"[i - 1], 64))).ToArray());

    [Fact] public void Request_requires_exact_unique_six_targets() { var request = Request() with { Targets = Request().Targets.Take(5).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("release_identity_invalid", error); }
    [Fact] public void Request_rejects_duplicate_service_ids() { var request = Request() with { Targets = Request().Targets.Select((t, i) => i == 5 ? t with { ServiceId = "svc-1" } : t).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_set_invalid", error); }
    [Fact] public void Journal_reconstructs_and_rejects_truncation() { string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".journal"); try { var journal = new FileHostUpdateExecutionJournal(path); journal.Append(new("a", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow)); Assert.Single(journal.Read("r")); File.WriteAllText(path, File.ReadAllText(path)[..^3]); Assert.Throws<InvalidDataException>(() => journal.Read("r")); } finally { if (File.Exists(path)) File.Delete(path); } }

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
            new FileHostUpdateExecutionJournal(path).Append(new("a", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow));
            new FileHostUpdateExecutionJournal(path).Append(new("a", "r", HostUpdateExecutionState.Preflight, "preflight_ok", DateTimeOffset.UtcNow));
            new FileHostUpdateExecutionJournal(path).Append(new("a", "r", HostUpdateExecutionState.Draining, "drain_ok", DateTimeOffset.UtcNow));

            IReadOnlyList<HostUpdateExecutionActivity> entries = new FileHostUpdateExecutionJournal(path).Read("r");

            Assert.Equal(3, entries.Count);
            Assert.Equal(
                new[] { HostUpdateExecutionState.Accepted, HostUpdateExecutionState.Preflight, HostUpdateExecutionState.Draining },
                entries.Select(e => e.State));
            Assert.Equal(3, File.ReadAllLines(path).Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
    [Fact] public async Task Executor_persists_transition_order_and_completion_async() { var steps = new FakeSteps(); var journal = new MemoryJournal(); var executor = new HostUpdateExecutor(steps, journal, new NoopLock()); var result = await executor.ExecuteAsync(Request()); Assert.True(result.Succeeded); Assert.Equal(new[] { "preflight", "drain", "fence", "backup", "migration", "apply", "verify" }, steps.Calls); }

    private sealed class NoopLock : IHostUpdateExecutionLock { public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => new Lease(); private sealed class Lease : IHostUpdateExecutionLease { public void Dispose() { } } }
    private sealed class MemoryJournal : IHostUpdateExecutionJournal { private readonly List<HostUpdateExecutionActivity> entries = []; public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => entries.Where(e => e.ReleaseId == releaseId).ToArray(); public void Append(HostUpdateExecutionActivity activity) => entries.Add(activity); }
    private sealed class FakeSteps : IHostUpdateExecutionSteps { public List<string> Calls { get; } = []; public Task PreflightAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("preflight"); public Task DrainAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("drain"); public Task FenceAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("fence"); public Task BackupAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("backup"); public Task MigrateAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("migration"); public Task ApplyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("apply"); public Task VerifyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("verify"); private Task AddAsync(string value) { Calls.Add(value); return Task.CompletedTask; } }
}



