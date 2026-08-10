using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Priority hint for the underlying provider. <c>Alert</c> maps to APNs
/// <c>apns-priority: 10</c>; <c>Background</c> maps to <c>5</c>. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public enum NativePushPriority
{
    /// <summary>User-visible alert; wakes the device immediately.</summary>
    Alert = 0,

    /// <summary>Content-available background delivery; used for silent invalidation on resolve.</summary>
    Background = 1,
}

/// <summary>
/// Fully typed payload the delivery service hands to <see cref="INativePushSender"/>. The
/// wire representation is provider-specific (APS JSON for APNs; JSON post for relay), but
/// this envelope is the single canonical shape kept in tests. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
/// <param name="OriginServerId">
/// Canonical UUID of the PrintFarmer server instance that generated this payload (issue
/// #1407). Required for every newly generated envelope; senders validate this before
/// serializing and refuse to send rather than substitute a different value when it is
/// missing or non-canonical.
/// </param>
public sealed record NativePushEnvelope(
    string DeviceTokenId,
    string Token,
    string Platform,
    string Environment,
    string? AppBundleId,
    string Category,
    string ThreadId,
    string? Title,
    string? Subtitle,
    string? Body,
    string AttentionItemId,
    AttentionKind AttentionKind,
    AttentionChangeKind ChangeKind,
    Guid PrinterId,
    Guid? JobId,
    int? ToolheadIndex,
    string DeepLink,
    NativePushPriority Priority,
    DateTime? ExpiresAtUtc,
    IReadOnlyList<string> ActionIds,
    string OriginServerId);
