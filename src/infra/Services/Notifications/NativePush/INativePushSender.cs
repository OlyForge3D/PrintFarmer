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
/// Implementations must invoke <see cref="INativePushTransportStart.TryStart"/> exactly
/// once after all local preparation has completed and immediately before initiating the
/// provider transport. A veto must prevent any provider call.
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
/// its lifecycle is no longer current.
/// </summary>
public interface INativePushTransportStart
{
    /// <summary>Signals an imminent provider call and returns whether it may proceed.</summary>
    NativePushTransportStartDecision TryStart();
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

    public NativePushTransportStartDecision TryStart() => NativePushTransportStartDecision.Permit();
}
