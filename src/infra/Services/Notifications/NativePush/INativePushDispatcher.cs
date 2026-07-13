using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Fan-out entry point invoked by <c>AttentionBroadcaster</c> after a SignalR broadcast.
/// See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public interface INativePushDispatcher
{
    /// <summary>
    /// Attempts to deliver a native push for the given attention change to every eligible
    /// recipient (or a single recipient when <paramref name="targetUserId"/> is supplied).
    /// Implementations MUST NOT throw — attention broadcast reliability is more important
    /// than push delivery — but MUST honour cancellation.
    /// </summary>
    Task DispatchAsync(
        string attentionItemId,
        AttentionChangeKind changeKind,
        Guid? targetUserId,
        CancellationToken cancellationToken = default);
}
