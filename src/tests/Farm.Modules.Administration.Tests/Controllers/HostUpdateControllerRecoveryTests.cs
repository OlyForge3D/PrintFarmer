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
    public async Task Recovery_rejects_journal_binding_hash_mismatch_before_side_effects(string mutation)
    {
        HostUpdateExecutionRequest request = Request();
        HostUpdateExecutionRequest changed = Mutate(request, mutation);
        MemoryJournal journal = new(new HostUpdateExecutionActivity("failure", request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(changed),
            RequestBinding = request,
        });
        RecordingRecovery recovery = new();
        HostUpdateController controller = new(new NoopExecutor(), new NoopResolver(), journal, recovery);

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, null, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Recovery_rejects_fresh_request_id_mismatch_before_side_effects()
    {
        HostUpdateExecutionRequest request = Request();
        MemoryJournal journal = new(new HostUpdateExecutionActivity("failure", request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        });
        RecordingRecovery recovery = new();
        HostUpdateController controller = new(new NoopExecutor(), new NoopResolver(), journal, recovery);

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, new HostUpdateRecoveryRequestBody("other-request"), default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Recovery_rejects_missing_or_mixed_binding_before_side_effects()
    {
        HostUpdateExecutionRequest request = Request();
        HostUpdateExecutionActivity missing = new("missing", request.ReleaseId, HostUpdateExecutionState.Applying, "apply:before", DateTimeOffset.UtcNow);
        HostUpdateExecutionActivity bound = new("failure", request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        };
        RecordingRecovery recovery = new();
        HostUpdateController controller = new(new NoopExecutor(), new NoopResolver(), new MemoryJournal(missing, bound), recovery);

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, null, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Recovery_accepts_exact_shared_journal_binding_only_then_invokes_coordinator()
    {
        HostUpdateExecutionRequest request = Request();
        MemoryJournal journal = new(new HostUpdateExecutionActivity("failure", request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        });
        RecordingRecovery recovery = new();
        HostUpdateController controller = new(new NoopExecutor(), new NoopResolver(), journal, recovery);

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, new HostUpdateRecoveryRequestBody(request.RequestId), default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, recovery.Calls);
    }


    [Fact]
    public async Task Recovery_returns_503_when_recovery_port_unavailable()
    {
        HostUpdateExecutionRequest request = Request();
        MemoryJournal journal = new(new HostUpdateExecutionActivity("failure", request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        });
        HostUpdateController controller = new(new NoopExecutor(), new NoopResolver(), journal, new UnavailableHostUpdateRecoveryCoordinator());

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync(request.ReleaseId, null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task JournaledRecovery_appends_started_and_terminal_success_and_blocks_replay()
    {
        HostUpdateExecutionRequest request = Request();
        MutableJournal journal = MutableJournal.InRecovery(request);
        JournaledHostUpdateRecoveryCoordinator coordinator = new(journal, new MemoryRecoveryLeaseProvider(), new FixedRecovery(HostUpdateRecoveryOutcome.RolledBack, "image_only_rollback"));

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(request, journal.Read(request.ReleaseId), default);
        HostUpdateRecoveryResult replay = await coordinator.RecoverAsync(request, journal.Read(request.ReleaseId), default);

        Assert.Equal(HostUpdateRecoveryOutcome.RolledBack, result.Outcome);
        Assert.Contains(journal.Read(request.ReleaseId), activity => activity.Phase == "recovery:started" && activity.RequestBindingHash == HostUpdateRequestBinding.Compute(request));
        Assert.Contains(journal.Read(request.ReleaseId), activity => activity.State == HostUpdateExecutionState.Completed && activity.Phase == "recovery:rolled_back");
        Assert.Equal("not_in_recovery", replay.Detail);
    }

    [Fact]
    public async Task JournaledRecovery_reconstructs_after_restart_and_rejects_binding_mismatch()
    {
        HostUpdateExecutionRequest request = Request();
        MutableJournal journal = MutableJournal.InRecovery(request);
        JournaledHostUpdateRecoveryCoordinator restarted = new(journal, new MemoryRecoveryLeaseProvider(), new FixedRecovery(HostUpdateRecoveryOutcome.NeedsOperator, "no_backup_available"));

        HostUpdateRecoveryResult result = await restarted.RecoverAsync(request, Array.Empty<HostUpdateExecutionActivity>(), default);
        HostUpdateRecoveryResult mismatch = await restarted.RecoverAsync(request with { PolicyRevision = 9 }, journal.Read(request.ReleaseId), default);

        Assert.Equal("no_backup_available", result.Detail);
        Assert.Equal("recovery_binding_mismatch", mismatch.Detail);
        Assert.Contains(journal.Read(request.ReleaseId), activity => activity.Phase == "recovery:needs_operator");
    }

    [Fact]
    public async Task JournaledRecovery_prevents_concurrent_recovery()
    {
        HostUpdateExecutionRequest request = Request();
        MutableJournal journal = MutableJournal.InRecovery(request);
        BlockingRecovery inner = new();
        JournaledHostUpdateRecoveryCoordinator coordinator = new(journal, new SingleRecoveryLeaseProvider(), inner);

        Task<HostUpdateRecoveryResult> first = Task.Run(() => coordinator.RecoverAsync(request, journal.Read(request.ReleaseId), default));
        Assert.True(inner.Started.Wait(TimeSpan.FromSeconds(5)), "Recovery did not start before timeout.");
        HostUpdateRecoveryResult second = await coordinator.RecoverAsync(request, journal.Read(request.ReleaseId), default);
        inner.Release.SetResult();
        await first;

        Assert.Equal("recovery_already_running", second.Detail);
    }


    [Fact]
    public async Task JournaledRecovery_persists_interrupted_terminal_outcome_when_inner_recovery_is_canceled_and_fresh_coordinator_sees_it()
    {
        HostUpdateExecutionRequest request = Request();
        MutableJournal journal = MutableJournal.InRecovery(request);
        CancelingRecovery inner = new();
        JournaledHostUpdateRecoveryCoordinator coordinator = new(journal, new MemoryRecoveryLeaseProvider(), inner);
        using CancellationTokenSource cts = new();
        inner.CancelWith(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RecoverAsync(request, journal.Read(request.ReleaseId), cts.Token));

        Assert.Contains(journal.Read(request.ReleaseId), activity => activity.Phase == "recovery:started" && activity.RequestBindingHash == HostUpdateRequestBinding.Compute(request));
        Assert.Contains(journal.Read(request.ReleaseId), activity => activity.State == HostUpdateExecutionState.RecoveryRequired && activity.Phase == "recovery:interrupted" && activity.RequestBindingHash == HostUpdateRequestBinding.Compute(request));

        // Fresh coordinator reconstruction (for example, after a process restart) must observe the
        // durable terminal entry rather than the open-ended "recovery:started" and must still be able
        // to admit a subsequent recovery attempt bound to the same request.
        JournaledHostUpdateRecoveryCoordinator reconstructed = new(journal, new MemoryRecoveryLeaseProvider(), new FixedRecovery(HostUpdateRecoveryOutcome.NeedsOperator, "no_backup_available"));
        HostUpdateRecoveryResult retried = await reconstructed.RecoverAsync(request, journal.Read(request.ReleaseId), default);

        Assert.Equal("no_backup_available", retried.Detail);
    }

    [Fact]
    public async Task JournaledRecovery_persists_unknown_terminal_outcome_when_inner_recovery_throws_and_fresh_coordinator_sees_it()
    {
        HostUpdateExecutionRequest request = Request();
        MutableJournal journal = MutableJournal.InRecovery(request);
        JournaledHostUpdateRecoveryCoordinator coordinator = new(journal, new MemoryRecoveryLeaseProvider(), new ThrowingRecovery());

        HostUpdateRecoveryResult result = await coordinator.RecoverAsync(request, journal.Read(request.ReleaseId), default);

        Assert.Equal(HostUpdateRecoveryOutcome.NeedsOperator, result.Outcome);
        Assert.Equal("recovery_unknown_failure", result.Detail);
        Assert.Contains(journal.Read(request.ReleaseId), activity => activity.Phase == "recovery:started" && activity.RequestBindingHash == HostUpdateRequestBinding.Compute(request));
        Assert.Contains(journal.Read(request.ReleaseId), activity => activity.State == HostUpdateExecutionState.RecoveryRequired && activity.Phase == "recovery:unknown" && activity.RequestBindingHash == HostUpdateRequestBinding.Compute(request));

        JournaledHostUpdateRecoveryCoordinator reconstructed = new(journal, new MemoryRecoveryLeaseProvider(), new FixedRecovery(HostUpdateRecoveryOutcome.NeedsOperator, "no_backup_available"));
        HostUpdateRecoveryResult retried = await reconstructed.RecoverAsync(request, journal.Read(request.ReleaseId), default);

        Assert.Equal("no_backup_available", retried.Detail);
    }


    private static HostUpdateExecutionRequest Request() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, Targets())
    {
        RequestId = "request-1",
        TrustRoot = "trust-root",
        PolicyRevision = 1,
        PolicyFingerprint = "policy",
        HostPlatform = "linux-amd64",
        AuthorizationKind = HostUpdateAuthorizationKind.Manual,
    };

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
        public IReadOnlyList<string> ListReleaseIds() => activities.Select(activity => activity.ReleaseId).Distinct(StringComparer.Ordinal).ToArray();
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


    private sealed class MutableJournal : IHostUpdateExecutionJournal
    {
        private readonly List<HostUpdateExecutionActivity> activities;

        private MutableJournal(IEnumerable<HostUpdateExecutionActivity> activities) => this.activities = activities.ToList();

        public static MutableJournal InRecovery(HostUpdateExecutionRequest request) => new([new HostUpdateExecutionActivity("failure", request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "failure", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        }]);

        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => activities.Where(activity => activity.ReleaseId == releaseId).ToArray();

        public IReadOnlyList<string> ListReleaseIds() => activities.Select(activity => activity.ReleaseId).Distinct(StringComparer.Ordinal).ToArray();

        public void Append(HostUpdateExecutionActivity activity) => activities.Add(activity);
    }

    private sealed class FixedRecovery(HostUpdateRecoveryOutcome outcome, string detail) : IHostUpdateRecoveryCoordinator
    {
        public Task<HostUpdateRecoveryResult> RecoverAsync(HostUpdateExecutionRequest failedRequest, IReadOnlyList<HostUpdateExecutionActivity> activities, CancellationToken cancellationToken) =>
            Task.FromResult(new HostUpdateRecoveryResult(outcome, detail));
    }

    private sealed class CancelingRecovery : IHostUpdateRecoveryCoordinator
    {
        private CancellationTokenSource? cancelWith;

        public void CancelWith(CancellationTokenSource cts) => cancelWith = cts;

        public Task<HostUpdateRecoveryResult> RecoverAsync(HostUpdateExecutionRequest failedRequest, IReadOnlyList<HostUpdateExecutionActivity> activities, CancellationToken cancellationToken)
        {
            cancelWith?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "unreachable"));
        }
    }

    private sealed class ThrowingRecovery : IHostUpdateRecoveryCoordinator
    {
        public Task<HostUpdateRecoveryResult> RecoverAsync(HostUpdateExecutionRequest failedRequest, IReadOnlyList<HostUpdateExecutionActivity> activities, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("physical recovery is not implemented");
    }

    private sealed class BlockingRecovery : IHostUpdateRecoveryCoordinator
    {
        public ManualResetEventSlim Started { get; } = new();

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HostUpdateRecoveryResult> RecoverAsync(HostUpdateExecutionRequest failedRequest, IReadOnlyList<HostUpdateExecutionActivity> activities, CancellationToken cancellationToken)
        {
            Started.Set();
            await Release.Task.WaitAsync(cancellationToken);
            return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "blocked");
        }
    }

    private sealed class MemoryRecoveryLeaseProvider : IHostUpdateRecoveryLeaseProvider
    {
        public IHostUpdateRecoveryLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => new Lease();

        private sealed class Lease : IHostUpdateRecoveryLease
        {
            public void Dispose() { }
        }
    }

    private sealed class SingleRecoveryLeaseProvider : IHostUpdateRecoveryLeaseProvider
    {
        private int active;

        public IHostUpdateRecoveryLease Acquire(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            {
                throw new TimeoutException();
            }

            return new Lease(this);
        }

        private sealed class Lease(SingleRecoveryLeaseProvider owner) : IHostUpdateRecoveryLease
        {
            public void Dispose() => Volatile.Write(ref owner.active, 0);
        }
    }

    private sealed class NoopExecutor : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopResolver : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) => throw new NotSupportedException();
        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) => throw new NotSupportedException();
    }
}
