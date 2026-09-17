#pragma warning disable IDISP007
namespace Farm.Infrastructure.Services.HostUpdates;

public interface IHostUpdateRecoveryLease : IDisposable
{
}

public interface IHostUpdateRecoveryLeaseProvider
{
    IHostUpdateRecoveryLease Acquire(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class FileHostUpdateRecoveryLeaseProvider(string path) : IHostUpdateRecoveryLeaseProvider
{
    private readonly FileHostUpdateExecutionLock _inner = new(path);

    public IHostUpdateRecoveryLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) =>
        new Lease(_inner.Acquire(timeout, cancellationToken));

    private sealed class Lease(IHostUpdateExecutionLease inner) : IHostUpdateRecoveryLease
    {
        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Serializes recovery attempts, journals intent/outcome, and delegates side effects to a concrete recovery port.</summary>
public sealed class JournaledHostUpdateRecoveryCoordinator(
    IHostUpdateExecutionJournal journal,
    IHostUpdateRecoveryLeaseProvider leaseProvider,
    IHostUpdateRecoveryCoordinator inner) : IHostUpdateRecoveryCoordinator
{
    public async Task<HostUpdateRecoveryResult> RecoverAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedRequest);
        ArgumentNullException.ThrowIfNull(activities);
        string bindingHash = HostUpdateRequestBinding.Compute(failedRequest);
        IHostUpdateRecoveryLease lease;
        try
        {
            lease = leaseProvider.Acquire(TimeSpan.FromSeconds(1), cancellationToken);
        }
        catch (TimeoutException)
        {
            return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "recovery_already_running");
        }

        using (lease)
        {
            IReadOnlyList<HostUpdateExecutionActivity> current = journal.Read(failedRequest.ReleaseId);
            if (current.Count > 0)
            {
                activities = current;
            }

            if (activities.Count == 0 || activities[^1].State != HostUpdateExecutionState.RecoveryRequired)
            {
                return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "not_in_recovery");
            }

            if (activities.Any(activity => !string.Equals(activity.RequestBindingHash, bindingHash, StringComparison.Ordinal) || activity.RequestBinding is null || !string.Equals(HostUpdateRequestBinding.Compute(activity.RequestBinding), bindingHash, StringComparison.Ordinal)))
            {
                return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "recovery_binding_mismatch");
            }

            Append(failedRequest, HostUpdateExecutionState.RecoveryRequired, "recovery:started");
            HostUpdateRecoveryResult result = await inner.RecoverAsync(failedRequest, journal.Read(failedRequest.ReleaseId), cancellationToken).ConfigureAwait(false);
            HostUpdateExecutionState terminalState = result.Outcome == HostUpdateRecoveryOutcome.RolledBack
            ? HostUpdateExecutionState.Completed
            : HostUpdateExecutionState.RecoveryRequired;
            string terminalPhase = result.Outcome == HostUpdateRecoveryOutcome.RolledBack
            ? "recovery:rolled_back"
            : "recovery:needs_operator:" + result.Detail;
            Append(failedRequest, terminalState, terminalPhase);
            return result;
        }
    }

    private void Append(HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase)
    {
        journal.Append(new HostUpdateExecutionActivity(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        });
    }
}
