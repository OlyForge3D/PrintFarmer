namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Common marker for host-update ports that are registered for routability but unavailable.</summary>
public interface IHostUpdateAvailability
{
    bool IsAvailable { get; }

    string UnavailableReason { get; }
}

public sealed class UnavailableHostUpdateReplayStore : IHostUpdateReplayStore, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_replay_store_not_available";

    public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct) =>
        throw new NotSupportedException(UnavailableReason);
}

public sealed class UnavailableHostUpdatePolicyFence : IHostUpdatePolicyFence, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_policy_fence_not_available";

    public Task<bool> TryAdvanceAsync(long revision, string fingerprint, CancellationToken ct) =>
        throw new NotSupportedException(UnavailableReason);
}

public sealed class UnavailableHostUpdateAutomationPolicyRepository : IHostUpdateAutomationPolicyRepository, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_policy_repository_not_available";

    public HostUpdatePolicyReadResult Read() => new(false, new HostUpdateAutomationPolicy(), UnavailableReason);

    public Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy policy, long expectedRevision, CancellationToken ct) =>
        Task.FromResult(new HostUpdatePolicyReadResult(false, new HostUpdateAutomationPolicy(), UnavailableReason));
}

public sealed class UnavailableHostUpdateExecutionJournal : IHostUpdateExecutionJournal, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_execution_journal_not_available";

    public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => throw new NotSupportedException(UnavailableReason);

    public void Append(HostUpdateExecutionActivity activity) => throw new NotSupportedException(UnavailableReason);
}

public sealed class UnavailableHostUpdateExecutionRequestResolver : IHostUpdateExecutionRequestResolver, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_manual_authorization_not_available";

    public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
        throw new NotSupportedException(UnavailableReason);

    public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
        Task.FromResult(HostUpdateExecutionResolutionResult.Fail(UnavailableReason));
}

public sealed class UnavailableHostUpdateRecoveryCoordinator : IHostUpdateRecoveryCoordinator, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_recovery_not_available";

    public Task<HostUpdateRecoveryResult> RecoverAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, UnavailableReason));
}

public sealed class UnavailableHostUpdateExecutionLock : IHostUpdateExecutionLock, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_execution_lock_not_available";

    public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new NotSupportedException(UnavailableReason);
}
