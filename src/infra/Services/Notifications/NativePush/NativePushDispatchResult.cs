namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Terminal outcome of a single native-push send attempt against
/// <see cref="INativePushSender"/>. Retry / dedupe / rate-limit orchestration is the
/// delivery service's job, not the sender's. See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed record NativePushDispatchResult(
    bool Success,
    bool TokenInvalidated = false,
    bool IsTransient = false,
    string? Reason = null)
{
    /// <summary>Convenience: successful delivery.</summary>
    public static NativePushDispatchResult Delivered() => new(Success: true);

    /// <summary>Convenience: provider signaled a permanently invalid token.</summary>
    public static NativePushDispatchResult Invalidated(string reason)
        => new(Success: false, TokenInvalidated: true, Reason: reason);

    /// <summary>Convenience: transient failure eligible for retry.</summary>
    public static NativePushDispatchResult Transient(string reason)
        => new(Success: false, IsTransient: true, Reason: reason);

    /// <summary>Convenience: terminal non-retryable failure.</summary>
    public static NativePushDispatchResult Terminal(string reason)
        => new(Success: false, IsTransient: false, Reason: reason);

    /// <summary>Convenience: sender is not configured; caller should treat as skip.</summary>
    public static NativePushDispatchResult NotConfigured()
        => new(Success: false, IsTransient: false, Reason: "notConfigured");
}
