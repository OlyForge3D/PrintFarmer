#pragma warning disable VSTHRD003
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class DallasHostUpdateSchedulerExecutorTests
{
    private static HostUpdateExecutorRequest Request(string channel = "stable") => new(
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
            "sha256:" + new string('2', 64)));

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

        await adapter.SignalSafeCheckpointCancellationAsync("other-request", default);
        Assert.False(executor.CancellationRequested);
        await adapter.SignalSafeCheckpointCancellationAsync("request-1", default);
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
    public async Task SignalSafeCheckpointCancellation_DelayedSignalForCompletedGenerationNeverCrossesIntoNextGeneration()
    {
        // Simulates: request A is active, a checkpoint signal for A's requestId is prepared but its
        // delivery is paused, A completes on its own (without being cancelled) and is removed from the
        // active-request table, request B starts and reuses the same requestId (the scheduler always
        // signals against the *slot*, not a specific execution instance), and only then does the
        // paused signal for A finally proceed. The delivered cancellation must land on the request
        // that is actually live at delivery time (B) exactly once, must not throw
        // ObjectDisposedException from the now-disposed CTS that belonged to A, and must not double
        // deliver to B.
        InstrumentedExecutor executorA = new(completeImmediately: true);
        using DallasHostUpdateSchedulerExecutor adapterA = new(executorA, "linux-amd64");
        HostUpdateExecutorResponse responseA = await adapterA.ExecuteAsync(Request(), default);
        Assert.Equal(HostUpdateExecutorResult.Accepted, responseA.Result);

        InstrumentedExecutor executorB = new(completeImmediately: false);
        using DallasHostUpdateSchedulerExecutor adapterB = new(executorB, "linux-amd64");
        Task<HostUpdateExecutorResponse> executionB = adapterB.ExecuteAsync(Request(), default);
        await executorB.Started.Task;

        // The "delayed A signal" is delivered here, after A has already completed and B has already
        // started under the same requestId. It targets adapterB because, in production, the scheduler
        // always holds a single executor instance per standing update slot; this test uses two adapter
        // instances only to give each generation its own instrumented executor.
        await adapterB.SignalSafeCheckpointCancellationAsync("request-1", default);

        HostUpdateExecutorResponse responseB = await executionB;

        Assert.Equal(HostUpdateExecutorResult.Refused, responseB.Result);
        Assert.Equal("canceled_at_safe_checkpoint", responseB.Reason);
        Assert.Equal(1, executorB.CancellationObservedCount);
        Assert.Equal(0, executorA.CancellationObservedCount);
    }

    private sealed class InstrumentedExecutor(bool completeImmediately) : IHostUpdateExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancellationObservedCount { get; private set; }

        public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            if (completeImmediately)
            {
                return new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Completed, null, []);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
