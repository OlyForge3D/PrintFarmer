namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Raised when durable host-update authority cannot be read or committed safely.</summary>
public sealed class HostUpdateSubsystemUnavailableException : Exception
{
    public HostUpdateSubsystemUnavailableException(string code, Exception? innerException)
        : base(code, innerException)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
        {
            throw new ArgumentException("Availability codes must be stable tokens.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }

    public HostUpdateSubsystemUnavailableException()
        : this(HostUpdateAvailabilityCodes.StoreUnavailable)
    {
    }

    public HostUpdateSubsystemUnavailableException(string message)
        : this(message, null)
    {
    }
}

public static class HostUpdateAvailabilityCodes
{
    public const string StoreUnavailable = "host_update_durable_store_unavailable";

    /// <summary>Protected anti-replay state could not be read or committed.</summary>
    public const string ReplayUnavailable = "replay_unavailable";

    /// <summary>Durable automation policy could not be read.</summary>
    public const string PolicyUnavailable = "policy_unavailable";

    /// <summary>Durable one-time authorization state could not be read or committed.</summary>
    public const string AuthorizationStateUnavailable = "authorization_state_unavailable";

    /// <summary>Generic settings storage backing the manifest binding is unreachable.</summary>
    public const string ManifestBindingDatabaseUnavailable = "host_update_manifest_binding_database_unavailable";

    private static readonly HashSet<string> ExplicitDurableCodes = new(StringComparer.Ordinal)
    {
        StoreUnavailable,
        ReplayUnavailable,
        PolicyUnavailable,
        AuthorizationStateUnavailable,
        ManifestBindingDatabaseUnavailable,
    };

    /// <summary>
    /// Single classification for "the durable host-update subsystem could not answer". Callers
    /// map these to 503 so an operator never sees a conflict/stale-authorization 409 for a
    /// storage outage. Conflict codes (for example <c>candidate_replay_rejected</c>) are not
    /// durable-unavailable and keep their 409 semantics.
    /// </summary>
    public static bool IsDurableUnavailable(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        (ExplicitDurableCodes.Contains(code) ||
         code.EndsWith("_unavailable", StringComparison.Ordinal) ||
         code.EndsWith("_not_available", StringComparison.Ordinal) ||
         code.EndsWith("_not_provisioned", StringComparison.Ordinal));

    public static string SafeCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-')
            ? value
            : StoreUnavailable;
}

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

    public IReadOnlyList<string> ListReleaseIds() => throw new NotSupportedException(UnavailableReason);

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

public sealed class UnavailableHostUpdateRecoveryCoordinator : IHostUpdateRecoveryCoordinator, IHostUpdateRecoveryPlanner, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_recovery_not_available";

    public Task<HostUpdateRecoveryResult> RecoverAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, UnavailableReason));

    public Task<HostUpdateRecoveryPlan> PlanAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HostUpdateRecoveryPlan(HostUpdateRecoveryPlanKind.NeedsOperator, UnavailableReason, null, null));
}

public sealed class UnavailableHostUpdateExecutionLock : IHostUpdateExecutionLock, IHostUpdateAvailability
{
    public bool IsAvailable => false;

    public string UnavailableReason => "host_update_execution_lock_not_available";

    public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new NotSupportedException(UnavailableReason);
}
