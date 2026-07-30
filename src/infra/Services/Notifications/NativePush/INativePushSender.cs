namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Provider-abstract native-push sender. One implementation is registered at any time
/// (chosen by <see cref="NativePushSettings.Mode"/>). See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public interface INativePushSender
{
    /// <summary>Human-readable mode label used for metrics / logs (e.g. <c>relay</c>).</summary>
    string ModeName { get; }

    /// <summary>
    /// Sends a single push envelope. Implementations MUST NOT swallow cancellation, MUST
    /// classify permanent token invalidation (410 / BadDeviceToken / Unregistered) with
    /// <see cref="NativePushDispatchResult.TokenInvalidated"/> = <c>true</c>, and MUST NOT
    /// log or persist raw provider tokens.
    /// </summary>
    Task<NativePushDispatchResult> SendAsync(NativePushEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Native-push sender that participates in the dispatcher transport-start handshake.
/// Implementations must invoke <see cref="INativePushTransportStart.TryStartAsync"/>
/// exactly once after all local preparation has completed and immediately before
/// initiating the provider transport. A veto must prevent any provider call.
/// </summary>
public interface INativePushTransportSender : INativePushSender
{
    /// <summary>
    /// Sends one envelope after asking the dispatcher to commit the actual transport
    /// boundary. A denied decision is a no-transport result.
    /// </summary>
    Task<NativePushDispatchResult> SendAsync(
        NativePushEnvelope envelope,
        INativePushTransportStart transportStart,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatcher-owned callback a transport-aware sender invokes at its real provider
/// boundary. The callback atomically either commits that boundary or vetoes it because
/// its lifecycle is no longer current, the persisted feature gate is disabled, or the
/// caller has already cancelled.
/// </summary>
public interface INativePushTransportStart
{
    /// <summary>
    /// Signals an imminent provider call and returns whether it may proceed.
    ///
    /// <para>
    /// The dispatcher's production implementation performs the persisted operator
    /// feature-gate read asynchronously and OUTSIDE every in-memory lock — the
    /// authorization linearization point. Before the async gate resolves, no
    /// dispatcher/lifecycle/item/transport lock is held, so a slow DB round-trip
    /// cannot pin a thread-pool worker and cannot block unrelated concurrent
    /// transports. After the gate resolves, a narrow synchronous section
    /// atomically revalidates cancellation, reservation state, lifecycle current
    /// version, and prior start/veto before committing lifecycle ownership,
    /// dedupe/rate reservations, and the <c>Attempted</c> metric.
    /// </para>
    ///
    /// <para>
    /// Implementations MUST veto (return a non-permitted decision) when the
    /// caller's cancellation token is already signaled at the moment of this
    /// call — a pre-cancelled attempt must never commit dispatcher-owned
    /// lifecycle ownership, dedupe/rate reservations, or attempt metrics.
    /// A gate read that FAILS (DB down / repository throw) is treated as
    /// fail-closed veto (see the dispatcher implementation's logging); no
    /// provider call is admitted and reservations are rolled back.
    /// </para>
    ///
    /// <para>
    /// This is a defense-in-depth backstop: senders MUST ALSO check cancellation
    /// themselves immediately before awaiting this method, but the dispatcher's
    /// implementation never assumes a sender did so. Cancellation observed
    /// strictly AFTER a permitted decision is a genuine, already-committed
    /// attempt and is never retroactively vetoed.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation propagated from the caller's send-scope. Awaits are aborted
    /// promptly on signal and the returned decision vetoes with rollback.
    /// </param>
    Task<NativePushTransportStartDecision> TryStartAsync(CancellationToken cancellationToken);
}

/// <summary>Result of a sender's attempt to cross the native-push transport boundary.</summary>
public readonly record struct NativePushTransportStartDecision(bool IsPermitted)
{
    /// <summary>Creates a decision that permits the provider call.</summary>
    public static NativePushTransportStartDecision Permit() => new(true);

    /// <summary>Creates a decision that vetoes the provider call.</summary>
    public static NativePushTransportStartDecision Veto() => new(false);
}

internal sealed class AlwaysPermittedNativePushTransportStart : INativePushTransportStart
{
    public static AlwaysPermittedNativePushTransportStart Instance { get; } = new();

    public Task<NativePushTransportStartDecision> TryStartAsync(CancellationToken cancellationToken)
        => Task.FromResult(NativePushTransportStartDecision.Permit());
}
