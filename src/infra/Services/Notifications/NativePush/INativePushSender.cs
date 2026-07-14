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
