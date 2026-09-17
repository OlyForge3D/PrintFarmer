#pragma warning disable VSTHRD003
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class DallasHostUpdateSchedulerExecutorTests
{
    private static HostUpdateExecutorRequest Request(string channel = "stable", string operationToken = "operation-1") => new(
        "request-1",
        "release-1",
        new string('a', 40),
        42,
        "sha256:" + new string('b', 64),
        channel,
        "root-1",
        7,
        "policy-7",
        new HostUpdatePlatformDigests(
            "sha256:" + new string('c', 64),
            "sha256:" + new string('d', 64),
            "sha256:" + new string('e', 64),
            "sha256:" + new string('f', 64),
            "sha256:" + new string('1', 64),
            "sha256:" + new string('2', 64)),
        operationToken);

    [Fact]
    public async Task ExecuteAsync_MapsImmutableSchedulerBindingToDallasRequest()
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult("release-1", HostUpdateExecutionState.Completed, null, []));
        using DallasHostUpdateSchedulerExecutor adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request(), default);

        Assert.Equal(HostUpdateExecutorResult.Accepted, response.Result);
        HostUpdateExecutionRequest mapped = Assert.Single(executor.Requests);
        Assert.Equal("request-1", mapped.RequestId);
        Assert.Equal("root-1", mapped.TrustRoot);
        Assert.Equal(7, mapped.PolicyRevision);
        Assert.Equal("policy-7", mapped.PolicyFingerprint);
        Assert.Equal("linux-amd64", mapped.HostPlatform);
        Assert.Equal(HostUpdateExecutionChannel.Stable, mapped.Channel);
        Assert.Equal(HostUpdateExecutionRequest.RequiredServiceIds, mapped.Targets.Select(t => t.ServiceId).ToHashSet(StringComparer.Ordinal));
        Assert.All(mapped.Targets, target => Assert.Equal("linux-amd64", target.Platform));
        Assert.True(mapped.IsValid(out string error), error);
    }

    [Theory]
    [InlineData(HostUpdateExecutionState.RecoveryRequired, HostUpdateExecutorResult.RecoveryRequired)]
    [InlineData(HostUpdateExecutionState.Applying, HostUpdateExecutorResult.Refused)]
    public async Task ExecuteAsync_MapsDallasStateToSchedulerResult(HostUpdateExecutionState state, HostUpdateExecutorResult expected)
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult("release-1", state, "failure-code", []));
        using DallasHostUpdateSchedulerExecutor adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request(), default);

        Assert.Equal(expected, response.Result);
        Assert.Equal("failure-code", response.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_MapsInsiderChannelWithoutSharingStableNamespace()
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult("release-1", HostUpdateExecutionState.Completed, null, []));
        using DallasHostUpdateSchedulerExecutor adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request("insider"), default);

        Assert.Equal(HostUpdateExecutorResult.Accepted, response.Result);
        Assert.Equal(HostUpdateExecutionChannel.Insider, Assert.Single(executor.Requests).Channel);
    }

    [Fact]
    public async Task SignalSafeCheckpointCancellation_CancelsOnlyCurrentAutomaticRequest()
    {
        BlockingExecutor executor = new();
        using DallasHostUpdateSchedulerExecutor adapter = new(executor, "linux-amd64");
        Task<HostUpdateExecutorResponse> execution = adapter.ExecuteAsync(Request(), default);
        await executor.Started.Task;

        await adapter.SignalSafeCheckpointCancellationAsync(new HostUpdateCancellationSignal("other-request", "operation-1"), default);
        Assert.False(executor.CancellationRequested);
        await adapter.SignalSafeCheckpointCancellationAsync(new HostUpdateCancellationSignal("request-1", "operation-1"), default);
        HostUpdateExecutorResponse response = await execution;

        Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
        Assert.Equal("canceled_at_safe_checkpoint", response.Reason);
    }

    private sealed class CapturingExecutor(HostUpdateExecutionResult result) : IHostUpdateExecutor
    {
        public List<HostUpdateExecutionRequest> Requests { get; } = [];
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingExecutor : IHostUpdateExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationRequested { get; private set; }

        public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            CancellationRequested = true;
            return new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Applying, "canceled", []);
        }
    }

    [Fact]
    public async Task SignalSafeCheckpointCancellation_StaleGenerationSignalCannotCancelLaterRunWithSameRequestId()
    {
        // A request ID alone is not a safe cancellation address: the scheduler may legitimately run
        // the same request ID again after an earlier run finished. This pins the exact sequence:
        // a signal is captured while generation A is live, A completes on its own, generation B
        // starts under the SAME request ID, and only then is the captured signal delivered. B must
        // survive that stale delivery and must still be cancellable by its own signal, exactly once.
        GatedExecutor executor = new();
        using DallasHostUpdateSchedulerExecutor adapter = new(executor, "linux-amd64");

        Task<HostUpdateExecutorResponse> executionA = adapter.ExecuteAsync(Request(operationToken: "operation-a"), default);
        await executor.Started;
        HostUpdateCancellationSignal staleSignalForA = new("request-1", "operation-a");

        executor.Release();
        HostUpdateExecutorResponse responseA = await executionA;
        Assert.Equal(HostUpdateExecutorResult.Accepted, responseA.Result);
        Assert.Equal(0, executor.CancellationObservedCount);

        executor.ResetForNextGeneration();
        Task<HostUpdateExecutorResponse> executionB = adapter.ExecuteAsync(Request(operationToken: "operation-b"), default);
        await executor.Started;

        await adapter.SignalSafeCheckpointCancellationAsync(staleSignalForA, default);

        Assert.False(executionB.IsCompleted);
        Assert.Equal(0, executor.CancellationObservedCount);

        await adapter.SignalSafeCheckpointCancellationAsync(new HostUpdateCancellationSignal("request-1", "operation-b"), default);
        HostUpdateExecutorResponse responseB = await executionB;

        Assert.Equal(HostUpdateExecutorResult.Refused, responseB.Result);
        Assert.Equal("canceled_at_safe_checkpoint", responseB.Reason);
        Assert.Equal(1, executor.CancellationObservedCount);
    }

    [Fact]
    public async Task ExecuteAsync_RefusesRequestWithoutOperationToken()
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult("release-1", HostUpdateExecutionState.Completed, null, []));
        using DallasHostUpdateSchedulerExecutor adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request(operationToken: " "), default);

        Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
        Assert.Equal("operation_token_missing", response.Reason);
        Assert.Empty(executor.Requests);
    }

    /// <summary>Executor with an explicit pause hook so one generation can be held open across another.</summary>
    private sealed class GatedExecutor : IHostUpdateExecutor
    {
        private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public int CancellationObservedCount { get; private set; }

        public void Release() => _release.TrySetResult();

        public void ResetForNextGeneration()
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Task release = _release.Task;
            _started.TrySetResult();
            try
            {
                await release.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObservedCount++;
                throw;
            }

            return new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Completed, null, []);
        }
    }
}
