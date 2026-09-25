#pragma warning disable VSTHRD003
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateSchedulerExecutorAdapterTests
{
    private static HostUpdateExecutorRequest Request(string channel = "stable", string operationToken = "operation-1") => new(
        "request-1",
        channel == "insider" ? "insider:1.2.3-insider.4" : "stable:1.2.3",
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
    public async Task ExecuteAsync_MapsImmutableSchedulerBindingToHostUpdateRequest()
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult(Request().ReleaseId, HostUpdateExecutionState.Completed, null, []));
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request(), default);

        Assert.Equal(HostUpdateExecutorResult.Accepted, response.Result);
        HostUpdateExecutionRequest mapped = Assert.Single(executor.Requests);
        Assert.Equal("request-1", mapped.RequestId);
        Assert.Equal("stable:1.2.3", mapped.ReleaseId);
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
    public async Task ExecuteAsync_MapsHostUpdateStateToSchedulerResult(HostUpdateExecutionState state, HostUpdateExecutorResult expected)
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult(Request().ReleaseId, state, "failure-code", []));
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request(), default);

        Assert.Equal(expected, response.Result);
        Assert.Equal("failure-code", response.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_MapsInsiderChannelWithoutSharingStableNamespace()
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult(Request("insider").ReleaseId, HostUpdateExecutionState.Completed, null, []));
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request("insider"), default);

        Assert.Equal(HostUpdateExecutorResult.Accepted, response.Result);
        Assert.Equal("insider:1.2.3-insider.4", Assert.Single(executor.Requests).ReleaseId);
        Assert.Equal(HostUpdateExecutionChannel.Insider, Assert.Single(executor.Requests).Channel);
    }

    [Fact]
    public async Task SignalSafeCheckpointCancellation_CancelsOnlyCurrentAutomaticRequest()
    {
        BlockingExecutor executor = new();
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");
        Task<HostUpdateExecutorResponse> execution = adapter.ExecuteAsync(Request(), default);
        await executor.Started.Task;

        await adapter.SignalSafeCheckpointCancellationAsync(new HostUpdateCancellationSignal("other-request", "operation-1"), default);
        Assert.False(executor.CancellationRequested);
        await adapter.SignalSafeCheckpointCancellationAsync(new HostUpdateCancellationSignal("request-1", "operation-1"), default);
        HostUpdateExecutorResponse response = await execution;

        Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
        Assert.Equal("canceled_at_safe_checkpoint", response.Reason);
    }

    [Fact]
    public async Task PreArmCancellation_IsDeliveredWhenExecutionRegistersLater()
    {
        BlockingExecutor executor = new();
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");

        adapter.PreArmCancellation(new HostUpdateCancellationSignal("request-1", "operation-1"));
        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request(), default);

        Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
        Assert.Equal("canceled_at_safe_checkpoint", response.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LifecycleSetupThrows_ReleasesOperationAndAllowsRetryAsync(bool throwFromCancellationCallback)
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult(Request().ReleaseId, HostUpdateExecutionState.Completed, null, []));
        CancellationTokenSource? capturedCancellation = null;
        Task? capturedCompletion = null;
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64")
        {
            OnOperationRegisteredForTests = (cancellation, completion) =>
            {
                capturedCancellation = cancellation;
                capturedCompletion = completion;
                if (!throwFromCancellationCallback)
                {
                    throw new InvalidOperationException("lifecycle_setup_failed");
                }

                _ = cancellation.Token.Register(static () => throw new InvalidOperationException("lifecycle_setup_failed"));
            }
        };

        if (throwFromCancellationCallback)
        {
            adapter.PreArmCancellation(new HostUpdateCancellationSignal("request-1", "operation-1"));
        }

        Task<HostUpdateExecutorResponse> execution = adapter.ExecuteAsync(Request(), default);
        if (throwFromCancellationCallback)
        {
            AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() => execution);
            Assert.Equal("lifecycle_setup_failed", Assert.IsType<InvalidOperationException>(Assert.Single(exception.InnerExceptions)).Message);
        }
        else
        {
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
            Assert.Equal("lifecycle_setup_failed", exception.Message);
        }

        Assert.NotNull(capturedCancellation);
        Assert.Throws<ObjectDisposedException>(() => capturedCancellation.Token);
        Assert.NotNull(capturedCompletion);
        Assert.True(capturedCompletion.IsCompletedSuccessfully);
        Assert.Empty(executor.Requests);

        adapter.OnOperationRegisteredForTests = null;
        HostUpdateExecutorResponse retry = await Task.Run(
            () => adapter.ExecuteAsync(Request(operationToken: "operation-2"), default)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HostUpdateExecutorResult.Accepted, retry.Result);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateRequest_RefusesWithoutRemovingActiveOperationAsync()
    {
        GatedExecutor executor = new();
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");
        Task<HostUpdateExecutorResponse> execution = adapter.ExecuteAsync(Request(), default);
        await executor.Started;

        try
        {
            HostUpdateExecutorResponse duplicate = await adapter.ExecuteAsync(Request(operationToken: "operation-2"), default);

            Assert.Equal(HostUpdateExecutorResult.Refused, duplicate.Result);
            Assert.Equal("request_already_running", duplicate.Reason);
            await adapter.SignalSafeCheckpointCancellationAsync(new HostUpdateCancellationSignal("request-1", "operation-1"), default);
            HostUpdateExecutorResponse response = await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
            Assert.Equal("canceled_at_safe_checkpoint", response.Reason);
            Assert.Equal(1, executor.CancellationObservedCount);
        }
        finally
        {
            executor.Release();
            await execution;
        }
    }

    [Fact]
    public async Task PreArmCancellation_OverflowFailsClosed()
    {
        CapturingExecutor executor = new(new HostUpdateExecutionResult(Request().ReleaseId, HostUpdateExecutionState.Completed, null, []));
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");

        for (int i = 0; i < 16; i++)
        {
            adapter.PreArmCancellation(new HostUpdateCancellationSignal($"request-{i}", $"operation-{i}"));
        }

        adapter.PreArmCancellation(new HostUpdateCancellationSignal("request-overflow", "operation-overflow"));
        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(
            Request() with { RequestId = "request-overflow", OperationToken = "operation-overflow" },
            default);

        Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
        Assert.Equal("prearmed_cancellation_capacity_exceeded", response.Reason);
        Assert.Empty(executor.Requests);
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
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");

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
        CapturingExecutor executor = new(new HostUpdateExecutionResult(Request().ReleaseId, HostUpdateExecutionState.Completed, null, []));
        using HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");

        HostUpdateExecutorResponse response = await adapter.ExecuteAsync(Request(operationToken: " "), default);

        Assert.Equal(HostUpdateExecutorResult.Refused, response.Result);
        Assert.Equal("operation_token_missing", response.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndWaitsForActiveExecution()
    {
        CancellationResistantExecutor executor = new();
        HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");
        Task<HostUpdateExecutorResponse> execution = adapter.ExecuteAsync(Request(), default);
        await executor.Started.Task;

        Task disposal = adapter.DisposeAsync().AsTask();
        await executor.CancellationObserved.Task;

        Assert.False(disposal.IsCompleted);
        executor.Release.TrySetResult();
        await execution;
        await disposal;
        Assert.Equal(
            "executor_disposed",
            (await adapter.ExecuteAsync(Request(operationToken: "operation-2"), default)).Reason);
    }

    [Fact]
    public async Task DisposeAsync_ThrowingCancellationCallbackStillWaitsForActiveExecution()
    {
        ThrowingCancellationExecutor executor = new();
        HostUpdateSchedulerExecutorAdapter adapter = new(executor, "linux-amd64");
        Task<HostUpdateExecutorResponse> execution = adapter.ExecuteAsync(Request(), default);
        await executor.Started.Task;

        Task disposal = adapter.DisposeAsync().AsTask();
        await executor.CancellationObserved.Task;

        Assert.False(disposal.IsCompleted);
        executor.Release.TrySetResult();
        await execution;
        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() => disposal);
        Assert.StartsWith("host_update_shutdown_cancellation_failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains(exception.Flatten().InnerExceptions, error => error is InvalidOperationException { Message: "cancellation_callback_failed" });
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

    private sealed class CancellationResistantExecutor : IHostUpdateExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            await Release.Task;
            return new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Completed, null, []);
        }
    }

    private sealed class ThrowingCancellationExecutor : IHostUpdateExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
                throw new InvalidOperationException("cancellation_callback_failed");
            });
            await Release.Task;
            return new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Completed, null, []);
        }
    }
}
