namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Decides whether the daemon may hand any pull-delivered operation to the executor.</summary>
public interface IHostUpdateDaemonExecutionGate
{
    /// <summary>Returns <see langword="null"/> when execution is permitted, otherwise a fixed reason code.</summary>
    string? Evaluate();
}

/// <summary>
/// The only production gate (issue #3114). It has no constructor inputs, reads no configuration,
/// environment, API response or saved setting, and therefore cannot be switched on at runtime.
/// Replacing it is a later, owner-authorized code change gated on #2982.
/// </summary>
public sealed class DisabledHostUpdateDaemonExecutionGate : IHostUpdateDaemonExecutionGate
{
    public const string DisabledCode = "runtime_updates_disabled_pending_2982";

    public string? Evaluate() => DisabledCode;
}

public sealed record HostUpdateDaemonDispatchResult(bool Dispatched, string? RefusalCode, HostUpdateExecutionResult? Execution)
{
    public static HostUpdateDaemonDispatchResult Refused(string code) => new(false, code, null);
}

/// <summary>
/// The daemon's single path to execution. It adds no engine: after the gate it calls the existing
/// <see cref="IHostUpdateExecutor"/>, which takes the shared execution lock and writes the shared
/// journal, so daemon, CLI and manual attempts are serialized by the same lock. A held lock is
/// reported as <c>execution_lock_held</c>; the daemon never retries in a tight loop. Only a release
/// produced by <see cref="HostUpdateDaemonReleaseVerifier"/> can be dispatched, and only while that
/// verification is unexpired and the request names exactly the verified release and image set.
/// </summary>
public sealed class HostUpdateDaemonExecutionDispatcher(
    IHostUpdateDaemonExecutionGate gate,
    IHostUpdateExecutor executor,
    TimeProvider? timeProvider = null)
{
    public const string ExecutionLockHeldCode = "execution_lock_held";

    public const string VerificationExpiredCode = "verification_expired";

    public const string VerificationBindingMismatchCode = "verification_binding_mismatch";

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<HostUpdateDaemonDispatchResult> DispatchAsync(
        HostUpdateDaemonVerifiedRelease verified,
        HostUpdateExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        string? refusal = gate.Evaluate();
        if (refusal is not null)
        {
            return HostUpdateDaemonDispatchResult.Refused(refusal);
        }

        if (time.GetUtcNow() >= verified.ExpiresAt)
        {
            return HostUpdateDaemonDispatchResult.Refused(VerificationExpiredCode);
        }

        if (!verified.Matches(request))
        {
            return HostUpdateDaemonDispatchResult.Refused(VerificationBindingMismatchCode);
        }

        try
        {
            HostUpdateExecutionResult result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return new(true, null, result);
        }
        catch (TimeoutException)
        {
            return HostUpdateDaemonDispatchResult.Refused(ExecutionLockHeldCode);
        }
    }
}
