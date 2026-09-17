#pragma warning disable VSTHRD003
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateSchedulerTests
{
    [Fact]
    public async Task TickAsync_DefaultSettings_DoNotExecute()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(executor: executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.Disabled, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_StableOptIn_ExecutesOneImmutableRequest()
    {
        FakeExecutor executor = new();
        VerifiedHostUpdateCandidate candidate = Candidate();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(AutoEnabled: true), candidate, executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.NoCandidate, status.Reason);
        HostUpdateExecutorRequest request = Assert.Single(executor.Requests);
        Assert.Equal(candidate.ReleaseId, request.ReleaseId);
        Assert.Equal(candidate.SourceCommit, request.SourceCommit);
        Assert.Equal(candidate.Sequence, request.Sequence);
        Assert.Equal(candidate.ManifestDigest, request.ManifestDigest);
        Assert.Equal(candidate.Channel, request.Channel);
        Assert.Equal(candidate.PlatformDigests, request.PlatformDigests);
        Assert.True(request.IsValid);
    }

    [Fact]
    public async Task TickAsync_InsiderWithoutAcknowledgement_DoesNotExecute()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true, Channel: UpdateChannelSettings.InsiderChannel), Candidate(UpdateChannelSettings.InsiderChannel), executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.InsiderAcknowledgementRequired, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_InsiderAcknowledged_Executes()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true, Channel: UpdateChannelSettings.InsiderChannel, InsiderAcknowledged: true), Candidate(UpdateChannelSettings.InsiderChannel), executor);

        await scheduler.TickAsync();

        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_InvalidCacheAndClosedWindow_BackOffWithoutExecution()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate() with { CryptographicallyVerified = false, MaintenanceWindowOpen = false }, executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.CandidateInvalid, status.Reason);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_ExecutorFailure_BackOffsAndPreservesPolicyRevision()
    {
        FakeExecutor executor = new() { Response = new(HostUpdateExecutorResult.Failed, "failed") };
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true, PolicyRevision: 7), Candidate(), executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.ExecutorFailed, status.Reason);
        Assert.Equal(7, status.PolicyRevision);
        Assert.NotNull(status.NextPollAt);
    }

    [Fact]
    public async Task TickAsync_MissingReplayStore_FailsClosed()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate(), executor, new MissingReplayStore());

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.ReplayStoreUnavailable, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_ConcurrentCalls_DoNotOverlap()
    {
        BlockingExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate(), executor);
        Task<HostUpdateSchedulerStatus> first = scheduler.TickAsync();
        await executor.Started.Task;
        HostUpdateSchedulerStatus second = await scheduler.TickAsync();
        executor.Release.TrySetResult();
        await first;

        Assert.Equal(HostUpdateSchedulerReason.UpdateAlreadyRunning, second.Reason);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_HostShutdown_ReturnsCleanly()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate(), executor);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        HostUpdateSchedulerStatus status = await scheduler.TickAsync(cancellation.Token);

        Assert.Equal(HostUpdateSchedulerReason.HostShutdown, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task SignalSafeCheckpointCancellation_IsDelegatedAndManualIsNotExposed()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(executor: executor);

        await scheduler.SignalSafeCheckpointCancellationAsync();

        Assert.Equal("automatic", executor.CancelledRequestId);
    }

    [Fact]
    public async Task FileReplayStore_PersistsHighWaterAcrossRestartAndRejectsReplay()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "host-update-replay.json"), "{\"HighWaterByChannel\":{},\"RejectedIdentities\":[]}");
            FileHostUpdateReplayStore first = new(root);
            Assert.True(await first.TryAcceptAsync(Candidate(), default));
            FileHostUpdateReplayStore restarted = new(root);
            Assert.False(await restarted.TryAcceptAsync(Candidate(), default));
            Assert.False(await restarted.TryAcceptAsync(Candidate() with { Sequence = 0 }, default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FileReplayStore_MissingState_FailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new FileHostUpdateReplayStore(root).TryAcceptAsync(Candidate(), default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static HostUpdateScheduler Create(HostUpdateSchedulerSettings? settings = null, VerifiedHostUpdateCandidate? candidate = null, FakeExecutor? executor = null, IHostUpdateReplayStore? replay = null) =>
        new(new Settings(settings ?? new()), new Cache(candidate), replay ?? new MemoryReplayStore(), executor ?? new FakeExecutor(), new FixedClock(), new ZeroHostUpdateJitter());

    private static VerifiedHostUpdateCandidate Candidate(string channel = UpdateChannelSettings.StableChannel) => new("release-1", "commit-1", 1, "sha256:manifest", channel, true, true, true, true, true, true, new("api", "frontend", "worker", "slicer", "database", "host"));

    private sealed class Settings(HostUpdateSchedulerSettings value) : IHostUpdateSchedulerSettings { public HostUpdateSchedulerSettings Current { get; } = value; }
    private sealed class Cache(VerifiedHostUpdateCandidate? value) : IHostUpdateSchedulerCandidateCache { public VerifiedHostUpdateCandidate? Current { get; } = value; public string? LastError => null; }
    private sealed class FixedClock : IHostUpdateClock { public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }
    private class FakeExecutor : IHostUpdateSchedulerExecutor
    {
        public List<HostUpdateExecutorRequest> Requests { get; } = [];
        public HostUpdateExecutorResponse Response { get; set; } = new(HostUpdateExecutorResult.Accepted);
        public string? CancelledRequestId { get; private set; }
        public virtual Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(Response); }
        public virtual Task SignalSafeCheckpointCancellationAsync(string requestId, CancellationToken ct) { CancelledRequestId = requestId; return Task.CompletedTask; }
    }
    private sealed class BlockingExecutor : FakeExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct) { Requests.Add(request); Started.TrySetResult(); await Release.Task.WaitAsync(ct); return Response; }
    }
    private sealed class MemoryReplayStore : IHostUpdateReplayStore
    {
        private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);
        public Task<HostUpdateReplayState> LoadAsync(CancellationToken ct) => Task.FromResult(HostUpdateReplayState.Empty);
        public Task<bool> TryAcceptAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => Task.FromResult(_accepted.Add(candidate.Identity));
        public Task RecordRejectedAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => Task.CompletedTask;
        public Task RecordSupersededAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class MissingReplayStore : IHostUpdateReplayStore
    {
        public Task<HostUpdateReplayState> LoadAsync(CancellationToken ct) => throw new InvalidDataException("missing");
        public Task<bool> TryAcceptAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => throw new InvalidDataException("missing");
        public Task RecordRejectedAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => throw new InvalidDataException("missing");
        public Task RecordSupersededAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => throw new InvalidDataException("missing");
    }
}






