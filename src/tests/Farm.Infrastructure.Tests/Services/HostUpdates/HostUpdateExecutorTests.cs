#pragma warning disable VSTHRD003
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutorTests
{
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
        PolicyFingerprint = "policy-1",
        HostPlatform = "linux-amd64",
    };

    [Fact] public void Request_requires_exact_unique_six_targets() { var request = Request() with { Targets = Request().Targets.Take(5).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_invalid", error); }
    [Fact] public void Request_rejects_duplicate_service_ids() { var request = Request() with { Targets = Request().Targets.Select((t, i) => i == 5 ? t with { ServiceId = "svc-1" } : t).ToArray() }; Assert.False(request.IsValid(out var error)); Assert.Equal("target_set_invalid", error); }
    [Fact] public void Journal_reconstructs_and_rejects_truncation() { string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".journal"); try { var journal = new FileHostUpdateExecutionJournal(path); journal.Append(new("a", "r", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow)); Assert.Single(journal.Read("r")); File.WriteAllText(path, File.ReadAllText(path)[..^3]); Assert.Throws<InvalidDataException>(() => journal.Read("r")); } finally { if (File.Exists(path)) { File.Delete(path); } } }
    [Fact] public async Task Executor_persists_transition_order_and_completion_async() { var steps = new FakeSteps(); var journal = new MemoryJournal(); var executor = new HostUpdateExecutor(steps, journal, new NoopLock()); var result = await executor.ExecuteAsync(Request()); Assert.True(result.Succeeded); Assert.Equal(new[] { "preflight", "drain", "fence", "backup", "migration", "apply", "verify" }, steps.Calls); }
    [Fact]
    public async Task Executor_defers_safe_checkpoint_cancellation_until_after_unsafe_apply()
    {
        var steps = new CancellationObservingSteps();
        var executor = new HostUpdateExecutor(steps, new MemoryJournal(), new NoopLock());
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
        HostUpdateExecutionResult first = await new HostUpdateExecutor(failing, journal, new NoopLock()).ExecuteAsync(Request());
        HostUpdateExecutionResult restarted = await new HostUpdateExecutor(new FakeSteps(), journal, new NoopLock()).ExecuteAsync(Request());

        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, first.State);
        Assert.Equal(HostUpdateExecutionState.RecoveryRequired, restarted.State);
        Assert.Equal("recovery_required", restarted.FailureCode);
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
    public async Task Real_file_restart_marks_unmatched_unsafe_phase_recovery_without_reinvocation(HostUpdateExecutionState state, string phase)
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

            HostUpdateExecutionResult result = await new HostUpdateExecutor(steps, new FileHostUpdateExecutionJournal(path), new NoopLock()).ExecuteAsync(request);

            Assert.Equal(HostUpdateExecutionState.RecoveryRequired, result.State);
            Assert.Equal("unsafe_phase_interrupted", result.FailureCode);
            Assert.Empty(steps.Calls);
            HostUpdateExecutionActivity durable = Assert.Single(new FileHostUpdateExecutionJournal(path).Read(request.ReleaseId), activity => activity.State == HostUpdateExecutionState.RecoveryRequired);
            Assert.Equal("restart_uncertain:" + phase, durable.Phase);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
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
    private sealed class MemoryJournal : IHostUpdateExecutionJournal { private readonly List<HostUpdateExecutionActivity> entries = []; public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => entries.Where(e => e.ReleaseId == releaseId).ToArray(); public void Append(HostUpdateExecutionActivity activity) => entries.Add(activity); }
    private class FakeSteps : IHostUpdateExecutionSteps { public List<string> Calls { get; } = []; public Task PreflightAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("preflight"); public Task DrainAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("drain"); public Task FenceAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("fence"); public Task BackupAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("backup"); public Task MigrateAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("migration"); public virtual Task ApplyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("apply"); public Task VerifyAsync(HostUpdateExecutionRequest r, CancellationToken c) => AddAsync("verify"); private Task AddAsync(string value) { Calls.Add(value); return Task.CompletedTask; } }
}
