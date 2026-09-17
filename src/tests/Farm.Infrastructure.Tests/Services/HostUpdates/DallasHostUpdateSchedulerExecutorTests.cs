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
}
