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
    /// Returns the composed feed for <paramref name="userId"/>. Items snoozed by this
    /// user are excluded (subject to fresh-occurrence bypass); snoozes are strictly per-user.
    /// When <paramref name="isFarmAdmin"/> is <c>false</c>, maintenance items are filtered
    /// out <b>before</b> composition/pagination/totals so non-admins never see or page
    /// over maintenance ids, details, or counts.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="isFarmAdmin">
    /// True when the caller holds the <c>farm_admin</c> role. Callers MUST pass the
    /// authoritative claim; the service does not consult HttpContext.
    /// </param>
    /// <param name="page">1-based page number; values &lt;= 0 are clamped to 1.</param>
    /// <param name="pageSize">
    /// Page size; values &lt;= 0 fall back to the default and values are capped by
    /// <c>AttentionService.MaxPageSize</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AttentionFeedDto> GetFeedAsync(
        Guid userId,
        bool isFarmAdmin,
        int page = 1,
        int pageSize = 50,
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
