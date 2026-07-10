using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Dtos.Attention;

/// <summary>
/// Kind of attention item. Serialized as a camelCase string via
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>.
/// </summary>
/// <remarks>
/// Values are stable; new kinds are added without renaming existing ones so mobile
/// and web clients keep working across backend rollouts. <c>Runout</c> is reserved
/// for the F4 coverage source (#709) and defined here so clients can pattern-match
/// on it before the backend emits it.
/// </remarks>
public enum AttentionKind
{
    /// <summary>Auto-paused or high-confidence print failure.</summary>
    Failure = 0,

    /// <summary>Predicted filament runout (reserved for F4/#709).</summary>
    Runout = 1,

    /// <summary>Completed plate awaiting harvest.</summary>
    Harvest = 2,

    /// <summary>Active maintenance alert.</summary>
    Maintenance = 3,

    /// <summary>Enabled printer that is currently offline.</summary>
    Offline = 4,
}

/// <summary>
/// Severity of an attention item. Ordering: Critical &gt; Warning &gt; Info.
/// </summary>
public enum AttentionSeverity
{
    /// <summary>Informational; no operator action strictly required.</summary>
    Info = 0,

    /// <summary>Warning; operator attention recommended.</summary>
    Warning = 1,

    /// <summary>Critical; operator action required.</summary>
    Critical = 2,
}

/// <summary>
/// Typed action kinds. Clients MUST NOT synthesize raw URLs — they POST to
/// <c>POST /api/attention/{itemId}/actions/{actionKind}</c>, which the server
/// dispatches to the appropriate existing endpoint.
/// </summary>
public enum AttentionActionKind
{
    /// <summary>Pause the current print.</summary>
    Pause = 0,

    /// <summary>Resume a paused print.</summary>
    Resume = 1,

    /// <summary>Cancel the current print.</summary>
    Cancel = 2,

    /// <summary>Acknowledge (mark seen) a maintenance alert.</summary>
    Acknowledge = 3,

    /// <summary>Resolve (complete) a maintenance alert.</summary>
    Resolve = 4,

    /// <summary>Dismiss (ignore) a maintenance alert or failure.</summary>
    Dismiss = 5,

    /// <summary>Snooze the item for a duration; use the dedicated snooze endpoint.</summary>
    Snooze = 6,

    /// <summary>Harvest a completed plate (basic; F9/#714 will extend).</summary>
    Harvest = 7,
}

/// <summary>
/// A typed action a client can invoke on an attention item. Clients dispatch by
/// <see cref="Kind"/>, not by any URL the server returns.
/// </summary>
public sealed record AttentionActionDto(
    AttentionActionKind Kind,
    string Label,
    bool RequiresConfirmation);

/// <summary>
/// A single attention feed item. Feed items are computed on read; only per-user
/// snoozes are persisted.
/// </summary>
/// <param name="Id">Stable computed id of the form <c>{kind}:{sourceId}</c>.</param>
/// <param name="Kind">Attention kind (failure, maintenance, offline, harvest, runout).</param>
/// <param name="Severity">Severity level driving ordering and UI treatment.</param>
/// <param name="PrinterId">Owning printer id.</param>
/// <param name="PrinterName">Printer display name for compact rendering.</param>
/// <param name="Title">Short operator-facing title.</param>
/// <param name="Detail">Longer description including the actionable next step.</param>
/// <param name="OccurredAt">UTC timestamp used for tie-breaking within severity.</param>
/// <param name="Actions">Typed actions available to the caller.</param>
/// <param name="ToolheadIndex">Optional toolhead index for per-tool items.</param>
/// <param name="DeadlineAt">Optional deadline used for time-to-impact ordering.</param>
public sealed record AttentionItemDto(
    string Id,
    AttentionKind Kind,
    AttentionSeverity Severity,
    Guid PrinterId,
    string PrinterName,
    string Title,
    string Detail,
    DateTime OccurredAt,
    IReadOnlyList<AttentionActionDto> Actions,
    int? ToolheadIndex = null,
    DateTime? DeadlineAt = null);

/// <summary>
/// Response payload for <c>GET /api/attention</c>.
/// </summary>
/// <param name="Items">Severity-ordered items for the requested page.</param>
/// <param name="TotalCount">Total items across every page after snooze suppression.</param>
/// <param name="Page">1-based page number the caller received.</param>
/// <param name="PageSize">Item cap applied to <paramref name="Items"/>.</param>
/// <param name="TotalPages">Total pages available for <paramref name="TotalCount"/>.</param>
/// <param name="HealthyPrinterIds">
/// Enabled printers with no visible items. Page-independent; the full set is returned
/// on every page so the client can render the "N printers running normally" row without
/// paging.
/// </param>
public sealed record AttentionFeedDto(
    IReadOnlyList<AttentionItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<Guid> HealthyPrinterIds);

/// <summary>
/// Request payload for <c>POST /api/attention/{itemId}/snooze</c>.
/// </summary>
/// <remarks>
/// The optional <see cref="AttentionItemAnchorAtUtc"/> anchor is the item's
/// <see cref="AttentionItemDto.OccurredAt"/> at snooze time. When set, a future
/// occurrence of the same id whose <c>OccurredAt</c> is strictly greater will
/// bypass the snooze (guarantees a fresh incident cannot be suppressed by a
/// stale snooze). Callers may omit the anchor for legacy snoozes.
/// </remarks>
public sealed class SnoozeAttentionRequest
{
    /// <summary>UTC instant until which the item is snoozed. Must be in the future.</summary>
    [Required]
    public DateTime SnoozedUntilUtc { get; set; }

    /// <summary>
    /// Optional timing anchor — the <see cref="AttentionItemDto.OccurredAt"/> of the
    /// item at snooze time. Enables fresh-occurrence bypass; see the remarks on this
    /// request type.
    /// </summary>
    public DateTime? AttentionItemAnchorAtUtc { get; set; }
}
