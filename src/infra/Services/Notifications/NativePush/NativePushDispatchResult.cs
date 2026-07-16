namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>Identifies whether a terminal provider failure is evidence against a registration.</summary>
public enum NativePushFailureAttribution
{
    /// <summary>Provider/configuration/payload/unknown failure; never mutate token health.</summary>
    None = 0,

    /// <summary>The provider explicitly attributed the failure to this device token.</summary>
    DeviceToken = 1,
}

/// <summary>
/// Terminal outcome of a single native-push send attempt against
/// <see cref="INativePushSender"/>. Retry / dedupe / rate-limit orchestration is the
/// delivery service's job, not the sender's. See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed record NativePushDispatchResult(
    bool Success,
    bool TokenInvalidated = false,
    bool IsTransient = false,
    string? Reason = null,
    NativePushFailureAttribution FailureAttribution = NativePushFailureAttribution.None)
{
    /// <summary>Convenience: successful delivery.</summary>
    public static NativePushDispatchResult Delivered() => new(Success: true);

    /// <summary>Convenience: provider signaled a permanently invalid token.</summary>
    public static NativePushDispatchResult Invalidated(string reason)
        => new(Success: false, TokenInvalidated: true, Reason: reason);

    /// <summary>Convenience: transient failure eligible for retry.</summary>
    public static NativePushDispatchResult Transient(string reason)
        => new(Success: false, IsTransient: true, Reason: reason);

    /// <summary>Convenience: terminal non-retryable failure not attributable to a token.</summary>
    public static NativePushDispatchResult Terminal(string reason)
        => new(Success: false, IsTransient: false, Reason: reason);

    /// <summary>Terminal failure explicitly attributed by the provider to this token.</summary>
    public static NativePushDispatchResult TokenFailure(string reason)
        => new(
            Success: false,
            IsTransient: false,
            Reason: reason,
            FailureAttribution: NativePushFailureAttribution.DeviceToken);

    /// <summary>Convenience: sender is not configured; caller should treat as skip.</summary>
    public static NativePushDispatchResult NotConfigured()
        => new(Success: false, IsTransient: false, Reason: "notConfigured");

    /// <summary>
    /// Convenience: the dispatcher vetoed transport because the lifecycle was superseded.
    /// No provider call occurred.
    /// </summary>
    public static NativePushDispatchResult TransportStartVetoed()
        => new(Success: false, IsTransient: false, Reason: "transportStartVetoed");
}
