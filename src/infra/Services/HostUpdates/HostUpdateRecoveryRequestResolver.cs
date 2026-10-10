namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Result of reconstructing a failed request from durable journal evidence.</summary>
/// <param name="Request">The authoritative failed request when <paramref name="ErrorCode"/> is null.</param>
/// <param name="ErrorCode">
/// <c>no_history</c>, <c>not_in_recovery</c>, <c>recovery_binding_missing</c>,
/// <c>recovery_binding_mismatch</c> or <c>recovery_request_mismatch</c>.
/// </param>
public sealed record HostUpdateRecoveryRequestResolution(HostUpdateExecutionRequest? Request, string? ErrorCode)
{
    public bool Succeeded => Request is not null && ErrorCode is null;
}

/// <summary>
/// The single rule for reconstructing the immutable failed request that recovery may act on.
/// Shared by the admin API and the host-local CLI so neither can accept replacement release
/// material or a weaker binding check than the other.
/// </summary>
public static class HostUpdateRecoveryRequestResolver
{
    public const string NoHistory = "no_history";
    public const string NotInRecovery = "not_in_recovery";
    public const string BindingMissing = "recovery_binding_missing";
    public const string BindingMismatch = "recovery_binding_mismatch";
    public const string RequestMismatch = "recovery_request_mismatch";

    /// <summary>Accepts only the canonical release-id grammar used by the durable journal.</summary>
    public static bool IsValidReleaseId(string? releaseId) => HostUpdateValidation.IsReleaseId(releaseId);

    public static HostUpdateRecoveryRequestResolution Resolve(IReadOnlyList<HostUpdateExecutionActivity> activities, string? requestId)
    {
        ArgumentNullException.ThrowIfNull(activities);
        if (activities.Count == 0)
        {
            return new(null, NoHistory);
        }

        if (activities[^1].State != HostUpdateExecutionState.RecoveryRequired)
        {
            return new(null, NotInRecovery);
        }

        HostUpdateExecutionRequest[] journalRequests = activities.Select(activity => activity.RequestBinding)
            .Where(binding => binding is not null)
            .Select(binding => binding!)
            .ToArray();
        string[] journalBindings = activities.Select(activity => activity.RequestBindingHash)
            .Where(binding => !string.IsNullOrWhiteSpace(binding))
            .Select(binding => binding!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (journalRequests.Length != activities.Count || journalBindings.Length != 1 ||
            journalRequests.Any(binding => !string.Equals(HostUpdateRequestBinding.Compute(binding), journalBindings[0], StringComparison.Ordinal)))
        {
            return new(null, journalRequests.Length == 0 || journalBindings.Length == 0 ? BindingMissing : BindingMismatch);
        }

        // Every journal request already hashed to the single recorded binding above, so the
        // first entry is the authoritative failed request; no second equality sweep is needed.
        HostUpdateExecutionRequest failedRequest = journalRequests[0];
        if (!string.IsNullOrWhiteSpace(requestId) && !string.Equals(requestId, failedRequest.RequestId, StringComparison.Ordinal))
        {
            return new(null, RequestMismatch);
        }

        return new(failedRequest, null);
    }
}
