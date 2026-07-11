using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// Outcome of an attention action dispatch.
/// </summary>
public enum AttentionActionOutcome
{
    /// <summary>Action executed successfully.</summary>
    Ok = 0,

    /// <summary>Attention item was not found (may have already resolved).</summary>
    NotFound = 1,

    /// <summary>Requested action kind is not offered by the item.</summary>
    InvalidAction = 2,

    /// <summary>Downstream service refused the command (for example printer busy).</summary>
    Conflict = 3,

    /// <summary>Action failed with a downstream error.</summary>
    Failed = 4,

    /// <summary>Action is defined but not yet implemented server-side.</summary>
    NotImplemented = 5,
}

/// <summary>
/// Result of a snooze operation.
/// </summary>
public sealed record SnoozeResult(bool Success, string? Reason, AttentionSnooze? Snooze);

/// <summary>
/// Outcome of composing the attention feed. When <see cref="InvalidCursor"/> is <c>true</c>
/// the supplied cursor was malformed or unsupported and <see cref="Feed"/> is <c>null</c>;
/// the caller should surface an explicit validation error rather than restart from page 1.
/// </summary>
public sealed record AttentionFeedResult(AttentionFeedDto? Feed, bool InvalidCursor)
{
    /// <summary>Wraps a successfully composed feed.</summary>
    public static AttentionFeedResult Success(AttentionFeedDto feed) => new(feed, InvalidCursor: false);

    /// <summary>Signals that the supplied cursor could not be decoded.</summary>
    public static AttentionFeedResult BadCursor() => new(Feed: null, InvalidCursor: true);
}

/// <summary>
/// Result of an action-dispatch operation.
/// </summary>
public sealed record AttentionActionResult(AttentionActionOutcome Outcome, string? Reason);

/// <summary>
/// Orchestrates the unified attention feed: composes items across every registered
/// <see cref="IAttentionSource"/>, applies per-user snoozes, sorts by severity and
/// time-to-impact, and dispatches typed actions to the appropriate downstream service.
/// </summary>
public interface IAttentionService
{
    /// <summary>
    /// Returns the composed feed for <paramref name="userId"/> using cursor pagination.
    /// Items snoozed by this user are excluded (subject to fresh-occurrence bypass); snoozes
    /// are strictly per-user. When <paramref name="isFarmAdmin"/> is <c>false</c>, maintenance
    /// items are filtered out <b>before</b> composition/sort/cursor slicing so non-admins never
    /// see or page over maintenance ids, details, or counts.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="isFarmAdmin">
    /// True when the caller holds the <c>farm_admin</c> role. Callers MUST pass the
    /// authoritative claim; the service does not consult HttpContext.
    /// </param>
    /// <param name="cursor">
    /// Opaque cursor from a previous page's <c>nextCursor</c>, or <c>null</c> for the first
    /// page. A malformed/unsupported cursor yields <see cref="AttentionFeedResult.BadCursor"/>.
    /// </param>
    /// <param name="limit">
    /// Maximum items to return. Callers are responsible for validating the range (default
    /// <see cref="AttentionService.DefaultLimit"/>, max <see cref="AttentionService.MaxLimit"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AttentionFeedResult> GetFeedAsync(
        Guid userId,
        bool isFarmAdmin,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single item by its computed id for the given user (honouring snoozes),
    /// or <c>null</c> if the item is not present in the current feed. Ignores pagination.
    /// </summary>
    Task<AttentionItemDto?> FindItemAsync(Guid userId, string attentionItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a per-user snooze for <paramref name="attentionItemId"/>. If the item is
    /// not currently in the feed, still succeeds — snoozes are user intents, not FKs.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="attentionItemId">Computed attention item id.</param>
    /// <param name="snoozedUntilUtc">UTC instant after which the snooze expires.</param>
    /// <param name="attentionItemAnchorAtUtc">
    /// Optional timing anchor (typically the item's <c>OccurredAt</c> at snooze time).
    /// When <c>null</c>, the service resolves it from the current source snapshot.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SnoozeResult> SnoozeAsync(
        Guid userId,
        string attentionItemId,
        DateTime snoozedUntilUtc,
        DateTime? attentionItemAnchorAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Clears a snooze for the given user and item. No-op if none exists.</summary>
    Task<SnoozeResult> ClearSnoozeAsync(
        Guid userId,
        string attentionItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a typed action against the underlying source. Validates that
    /// <paramref name="actionKind"/> is offered by the item before dispatching. Callers
    /// MUST supply the authoritative role claim in <paramref name="isFarmAdmin"/>; the
    /// service will refuse maintenance actions for non-admin callers.
    /// </summary>
    Task<AttentionActionResult> ExecuteActionAsync(
        Guid userId,
        string userName,
        bool isFarmAdmin,
        string attentionItemId,
        AttentionActionKind actionKind,
        CancellationToken cancellationToken = default);
}
