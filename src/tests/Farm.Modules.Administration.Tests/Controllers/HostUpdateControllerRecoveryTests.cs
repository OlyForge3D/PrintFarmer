using Farm.Infrastructure.Services.HostUpdates;
using Farm.Modules.Administration.Controllers.Admin;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

public sealed class HostUpdateControllerRecoveryTests
{
    [Theory]
    [InlineData("digest")]
    [InlineData("target")]
    [InlineData("channel")]
    [InlineData("trust-root")]
    [InlineData("source-commit")]
    [InlineData("sequence")]
    [InlineData("policy-revision")]
    [InlineData("policy-fingerprint")]
    public async Task Recovery_rejects_every_immutable_binding_mismatch_before_side_effects(string mutation)
    {
        HostUpdateExecuteRequestBody body = Body();
        Assert.True(body.TryToExecutionRequest(out HostUpdateExecutionRequest? request, out _));
        HostUpdateExecutionRequest changed = Mutate(request!, mutation);
        MemoryJournal journal = new(new HostUpdateExecutionActivity("failure", request!.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(changed),
            RequestBinding = changed,
        });
        RecordingRecovery recovery = new();
        HostUpdateController controller = new(new NoopExecutor(), journal, recovery);

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, body, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Recovery_rejects_missing_or_mixed_binding_before_side_effects()
    {
        HostUpdateExecuteRequestBody body = Body();
        Assert.True(body.TryToExecutionRequest(out HostUpdateExecutionRequest? request, out _));
        HostUpdateExecutionActivity missing = new("missing", request!.ReleaseId, HostUpdateExecutionState.Applying, "apply:before", DateTimeOffset.UtcNow);
        HostUpdateExecutionActivity bound = new("failure", request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        };
        RecordingRecovery recovery = new();
        HostUpdateController controller = new(new NoopExecutor(), new MemoryJournal(missing, bound), recovery);

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, body, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Recovery_accepts_exact_shared_binding_only_then_invokes_coordinator()
    {
        HostUpdateExecuteRequestBody body = Body();
        Assert.True(body.TryToExecutionRequest(out HostUpdateExecutionRequest? request, out _));
        MemoryJournal journal = new(new HostUpdateExecutionActivity("failure", request!.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        });
        RecordingRecovery recovery = new();
        HostUpdateController controller = new(new NoopExecutor(), journal, recovery);

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, body, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, recovery.Calls);
    }

    private static HostUpdateExecuteRequestBody Body() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), "stable", Targets());

    private static HostUpdateExecutionRequest Mutate(HostUpdateExecutionRequest request, string mutation) => mutation switch
    {
        "digest" => request with { ManifestDigest = "sha256:" + new string('9', 64) },
        "target" => request with { Targets = request.Targets.Select((target, index) => index == 0 ? target with { ChildDigest = "sha256:" + new string('8', 64) } : target).ToArray() },
        "channel" => request with { Channel = HostUpdateExecutionChannel.Insider },
        "trust-root" => request with { TrustRoot = "different-trust-root" },
        "source-commit" => request with { SourceCommit = new string('c', 40) },
        "sequence" => request with { AuthenticatedSequence = 2 },
        "policy-revision" => request with { PolicyRevision = 2 },
        "policy-fingerprint" => request with { PolicyFingerprint = "different-policy" },
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };

    private static HostUpdateExecutionTarget[] Targets() =>
    [
        new("api", "linux-amd64", "sha256:" + new string('a', 64)),
        new("frontend", "linux-amd64", "sha256:" + new string('b', 64)),
        new("slicer-host", "linux-amd64", "sha256:" + new string('c', 64)),
        new("printer-discovery", "linux-amd64", "sha256:" + new string('d', 64)),
        new("orcaslicer-worker", "linux-amd64", "sha256:" + new string('e', 64)),
        new("monolith", "linux-amd64", "sha256:" + new string('f', 64)),
    ];

    private sealed class MemoryJournal(params HostUpdateExecutionActivity[] activities) : IHostUpdateExecutionJournal
    {
        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => activities.Where(activity => activity.ReleaseId == releaseId).ToArray();
        public void Append(HostUpdateExecutionActivity activity) => throw new NotSupportedException();
    }

    private sealed class RecordingRecovery : IHostUpdateRecoveryCoordinator
    {
        public int Calls { get; private set; }
        public Task<HostUpdateRecoveryResult> RecoverAsync(HostUpdateExecutionRequest failedRequest, IReadOnlyList<HostUpdateExecutionActivity> activities, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "test"));
        }
    }

    private sealed class NoopExecutor : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
