using Farm.Infrastructure.Services.HostUpdates;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Thrown inside the coordinator's own execution lock when the installed state it is about to
/// roll back to is not the one the CLI evaluated (and any reapproval token was bound to).
/// </summary>
#pragma warning disable CA1032 // Only ever constructed by the guard; standard constructors are not used.
internal sealed class HostUpdateRecoveryApprovalStaleException : InvalidOperationException
{
    public HostUpdateRecoveryApprovalStaleException()
        : base("installed_state_changed_since_evaluation")
    {
    }
}
#pragma warning restore CA1032

/// <summary>
/// Closes the window between the CLI's locked evaluation and the coordinator reacquiring the
/// execution lock (issue #2998 review R3048-H02). Once bound, the first installed-state read
/// (the coordinator's decision read, made under its lock and before any side effect) must hash
/// to exactly the evaluated state; otherwise recovery fails closed before restore or apply.
/// </summary>
internal sealed class ApprovalBoundInstalledHostStateStore(IInstalledHostStateStore inner) : IInstalledHostStateStore
{
    private Binding? _binding;

    /// <summary>
    /// Binds the next installed-state read to <paramref name="expectedInstalledStateHash"/>. When
    /// <paramref name="evidenceUnchanged"/> is supplied it is also evaluated at that read (under the
    /// coordinator's lock) and must return true, so other evaluated evidence cannot change unseen.
    /// </summary>
    public void Bind(string expectedInstalledStateHash, Func<bool>? evidenceUnchanged = null) =>
        _binding = new Binding(
            expectedInstalledStateHash ?? throw new ArgumentNullException(nameof(expectedInstalledStateHash)),
            evidenceUnchanged);

    public async Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken)
    {
        InstalledHostState? state = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        Binding? binding = Interlocked.Exchange(ref _binding, null);
        if (binding is not null &&
            (!string.Equals(binding.ExpectedHash, HostUpdateRecoveryDrift.InstalledStateHash(state), StringComparison.Ordinal) ||
             (binding.EvidenceUnchanged is not null && !binding.EvidenceUnchanged())))
        {
            throw new HostUpdateRecoveryApprovalStaleException();
        }

        return state;
    }

    public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken) =>
        inner.WriteAsync(state, cancellationToken);

    private sealed record Binding(string ExpectedHash, Func<bool>? EvidenceUnchanged);
}
